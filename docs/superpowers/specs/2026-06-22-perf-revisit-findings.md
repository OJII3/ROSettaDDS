# ROSettaDDS Unity Player Profiler 性能計測結果

## 計測環境

- **実行**: `tools/rosettadds-perf-runner` (nix devShell 環境, .NET 8, ROS 2 Humble)
- **Player**: Unity 6000.3.7f1 (StandaloneLinux64, mono backend)
- **計測日**: 2026-06-22
- **Run ID**: `20260622-024815`
- **Build**: uloop 経由で再ビルド (`Ros2Unity/artifacts/perf/test-build`)

## ビルドに関する重要な発見

`artifacts/perf/20260621-034324/build` (06-21 12:43 ビルド) の `RunUnityToRos2` IL を monodis / ilspycmd で逆アセンブルしたところ、`metrics.WriteSentinel(args.ReadyFile, "ready")` が `await publisher.WaitForMatchedAsync(...)` の**後**に配置されている (RosettaDDS.UnityPerfHarness.PerfPlayerEntry.cs:1157-1158 相当の IL オフセット)。

現在の `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:90-91` のソースは:
```csharp
participant.Start();
metrics.WriteSentinel(args.ReadyFile, "ready");  // ここにある
metrics.Event("ready");
bool matched = await publisher.WaitForMatchedAsync(...);
```

の順で WriteSentinel は await の前。

つまり、06-21 ビルドは `RunUnityToRos2` だけ旧仕様のまま固定されており (WriteSentinel が await の後ろ)、runner が ready sentinel を 20s 待ち続けて helper が起動せず、unity-to-ros2 系 scenario はマッチが成立しても `Publisher did not match a ROS 2 subscriber` で 10s timeout していました。06-22 に uloop 経由で再ビルドした Player (現在ソース) では WriteSentinel が await の前に戻っており、unity-to-ros2 scenario は正常動作。

## 計測結果 (`artifacts/perf/20260622-024815`)

| Scenario | payload | qos | messages | elapsed_ms | mps | sent/received | helper_rx | exit |
|----------|---------|-----|----------|-----------:|----:|--------------:|----------:|:----:|
| unity-to-ros2-reliable-32 | 32 B | reliable | 500 | 72.55 | **6 892** | 500 | 500 | ok |
| unity-to-ros2-reliable-1024 | 1024 B | reliable | 500 | 75.44 | **6 628** | 500 | 500 | ok |
| unity-to-ros2-best-effort-8192 | 8192 B | best_effort | 200 | 126.68 | **1 579** | 200 | 200 | ok |
| ros2-to-unity-reliable-32 | 32 B | reliable | 500 | 50.66 | **9 869** | 500 | 500 | ok |
| ros2-to-unity-reliable-1024 | 1024 B | reliable | 500 | 45.29 | **11 039** | 500 | 500 | ok |
| ros2-to-unity-best-effort-8192 | 8192 B | best_effort | 200 | 30 000+ | - | 90/200 | 200 | **timeout** |

## ボトルネック

### 1. unity-to-ros2 は ros2-to-unity より 30〜40 %遅い

reliable 32 B: 6 892 vs 9 869 msg/s。reliable 1024 B: 6 628 vs 11 039 msg/s。

**原因**: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:104-107` の publish loop が `await publisher.PublishAsync(message)` を逐次実行しているため、各 publish が前の publish の完了 (reliable なら subscriber からの ACK) を待つ。
```csharp
for (int i = 0; i < args.Messages; i++)
{
    await publisher.PublishAsync(message);
}
```

ROS 2 helper (`tools/ros2-perf-helper/src/ros2_perf_helper.cpp:246-251`) は同じ構造で `rclcpp::spin_some` しながら連続 publish しているが、ROS 2 は DDS 内臓の async/sync write で ACK を待つ実装になっているはず。差分は (a) `PublishAsync` 戻り値 (=ValueTask) を await するオーバーヘッドと (b) Unity の main thread 上で await するため SynchronizationContext を経由する分の context switch コスト。

main_thread_time_ns_last の平均:
- unity-to-ros2: ~110 000 ns / frame
- ros2-to-unity: ~78 000 ns / frame

→ unity-to-ros2 は main thread 占有時間が長い。これは publish loop が UnitySynchronizationContext に resume する分。

**対策案**:
- `PublishAsync` を `await` せず `ValueTask` を保持して最後に `await Task.WhenAll` する形にすれば、ACK 待ちの直列化を publisher queue depth に任せられる
- `ThreadPool` 上で await するため `ConfigureAwait(false)` を `PublishAsync` 経路で使う (Publisher 内部の実装次第)

### 2. 8 KB payload はすべての方向で遅い

- unity-to-ros2-reliable-32/1024: ~6 800 msg/s
- unity-to-ros2-best-effort-8192: 1 579 msg/s (約 1/4)
- ros2-to-unity-best-effort-8192: 30 s timeout で 90/200 (約 2.5 msg/s effective)

**8 KB = UDP MTU (loopback でも fragment する可能性大) + Cdr 8 KB allocate のオーバーヘッド**。
- `gc_used_memory_bytes_last` は best_effort-8192 で 4.6 MB まで膨らみ、他 scenario は 2.3〜3.2 MB
- GC 圧が上がり publish loop のレイテンシが増える

**対策案**:
- `tools/ros2-perf-helper/src/ros2_perf_helper.cpp:160-170` の `make_message` が毎回 `std::string(prefix + 'x'*N)` を生成。`std::string` 構築コスト 8 KB 毎に ~µs オーダー
- ROSettaDDS 側 serializer の `StringMessage` 8 KB 書き換えも毎 publish 同じバッファを `string.Concat` 系で構築 (Unity Editor の EditMode テストはこれで OK でも hot path では不利)
- `PayloadBytes` シナリオを一旦停止し、fragment 起因かどうかを UDP MTU 別に分割計測するべき
- best_effort は reliable より軽いはずだが差が小さい → reliable 経路は fragment 含む ACK retransmit のオーバーヘッドがある可能性

### 3. ros2-to-unity-best-effort-8192 が 30s timeout

`received=90/200` で Player 側 receive loop (`PerfPlayerEntry.cs:153-156`) の 30 s deadline に到達して終了。helper 側は 20 ms で 200 送信完了し終了している。

**つまり送信側は ~10 000 msg/s, 受信側は ~3 msg/s effective**。Unity 側で fragment 再組立てと 8 KB allocate を background receive thread で回しているが、main thread の `await Task.Delay(2)` ループはそこまで回っていない。

```
helper:  {"event":"done","sent":200,"elapsed_ms":19.9}
player:  {"event":"error","message":"... received 90/200."}
```

**対策案**:
- `PerfPlayerEntry.cs:152-156` の `Task.Delay(2)` poll はやめて、`AutoResetEvent` か `Volatile.Read` の spin-wait にすれば receive 完了検知が早くなる (ただし 8 KB fragment 化けで receiver queue が詰まる根本対策ではない)
- 8 KB fragment + best_effort の組合せで UDP packet loss が起きているなら、loopback でも OS の send buffer / receive buffer が小さい可能性がある (`/proc/sys/net/core/rmem_default` 等)
- reliable なら NACK できるが best_effort は再送されないので、helper 側で 1 packet に収まるサイズ (≒ 1500 B) に split して送るか、8 KB を別シナリオとして throttle (rate-hz) すべき

### 4. 8 KB payload の memory pressure

| scenario | gc_used_memory_bytes_last |
|----------|-------------------------:|
| reliable-32 | 2.3 MB |
| reliable-1024 | 2.6〜3.2 MB |
| best-effort-8192 | 4.6〜4.8 MB |

8 KB scenario は Unity 側で 2 倍近い GC 残量になっている。`GetTotalAllocatedBytes` 等の allocation rate を見たいが、現状 `PerfProfilerRecorders.cs` は `gc_used_memory_bytes` の last value しか取っていない。

**対策案**:
- `PerfProfilerRecorders.cs:18-22` に `Total Used Memory` (累積) と `GC Allocated In Frame` counter を追加して allocation rate 推移を記録する
- その上で 8 KB scenario で allocation が linear に増えているか確認

## 再現コマンド

```sh
nix develop
scripts/ros2/build_helper.sh
uloop execute-dynamic-code --project-path Ros2Unity --code 'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/test-build", "StandaloneLinux64", "mono"); return "ok";'
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/test-build --scenario all --capture-frames 1200
```

## 推奨アクション

1. **計測用の PublishAsync 経路を throughput mode に切り替える option 追加** (fire-and-forget, ConfigureAwait(false)) — これが無いうちは「実利用での最大値」を測れない
2. **`ros2-to-unity` の receive loop を spin-wait か event-driven に** — `Task.Delay(2)` が現状ボトルネック
3. **fragment 依存を切り分けた別 scenario 追加** (1400 B / 8000 B / 32 KiB)
4. **allocation rate を ProfilerRecorder に追加**
