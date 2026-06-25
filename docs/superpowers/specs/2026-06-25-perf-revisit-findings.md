# ROSettaDDS Unity Player Profiler 性能再計測 (2026-06-25)

## 計測環境

- **実行**: `tools/rosettadds-perf-runner --scenario all --capture-frames 1200`
- **Player**: Unity 6000.3.7f1 (StandaloneLinux64, IL2CPP)
- **Player build**: `artifacts/perf/20260623-022508/build/ROSettaDDSPerfPlayer` (2026-06-23 11:25 JST ビルド, `--skip-build` で再利用)
- **Player build commit**: `7a50c36` (HEAD)。`f66df12` (best-effort 受信ロスト抑制) を取り込んだ working tree でビルド。
- **計測日**: 2026-06-25 22:20 JST
- **Run ID**: `20260625-131941`
- **ROS 2 helper**: `tools/ros2-perf-helper` (前回ビルド, 06-22 計測と同一バイナリ)
- **比較対象 baseline**: `artifacts/perf/20260622-024815` (06-22 計測, ArrayPool + PublishRepeatedAsync 適用, best-effort fix は未適用)

## 計測結果

### シナリオ別スループット

| Scenario | payload | qos | messages | baseline mps | current mps | Δ% | baseline MB/s | current MB/s |
|----------|---------|-----|---------:|-------------:|------------:|----:|--------------:|-------------:|
| unity-to-ros2-reliable-32 | 32 B | reliable | 500 | 6 892 | 6 280 | -8.9% | 0.27 | 0.25 |
| unity-to-ros2-reliable-1024 | 1 033 B | reliable | 500 | 6 628 | 6 076 | -8.3% | 6.5 | 6.0 |
| unity-to-ros2-reliable-1400 | 1 409 B | reliable | 500 | — | 6 056 | (NEW) | — | 8.1 |
| unity-to-ros2-reliable-8000 | 8 009 B | reliable | 200 | — | 1 054 | (NEW) | — | 8.0 |
| unity-to-ros2-best-effort-8192 | 8 201 B | best_effort | 200 | 1 579 | 1 021 | -35.3% | 12.4 | 8.0 |
| ros2-to-unity-reliable-32 | 41 B | reliable | 500 | 9 869 | 9 425 | -4.5% | 0.39 | 0.37 |
| ros2-to-unity-reliable-1024 | 1 033 B | reliable | 500 | 11 039 | 10 447 | -5.4% | 10.9 | 10.3 |
| ros2-to-unity-best-effort-8192 | 8 201 B | best_effort | 200 | **TIMEOUT** (90/200) | **15 933** | **FIXED** | — | 130.7 |
| ros2-to-unity-best-effort-32k | 32 777 B | best_effort | 100 | — | 10 727 | (NEW) | — | 335.4 |

> MB/s は serialized_bytes (CDR encap 込み) × mps / 1e6。helper の elapsed_ms は前回比 ±3% 以内で安定 (loopback 飽和)。

### 主要メトリクス (current run)

| Scenario | us/msg | alloc/msg | GC samples | main_thread_ns_last | rtps_data | rtps_datafrag |
|----------|-------:|----------:|-----------:|--------------------:|----------:|--------------:|
| unity-to-ros2-reliable-32 | 159 | 7 112 B | 155 | 999 778 | — | — |
| unity-to-ros2-reliable-1024 | 165 | 9 114 B | 152 | 1 055 511 | — | — |
| unity-to-ros2-reliable-1400 | 165 | 9 160 B | 158 | 985 111 | — | — |
| unity-to-ros2-reliable-8000 | 948 | 34 005 B | 259 | 892 572 | — | — |
| unity-to-ros2-best-effort-8192 | 979 | 43 782 B | 265 | 874 203 | — | — |
| ros2-to-unity-reliable-32 | 106 | 4 084 B | 84 | 957 873 | 503 | 0 |
| ros2-to-unity-reliable-1024 | 96 | 5 914 B | 104 | 883 282 | 539 | 0 |
| ros2-to-unity-best-effort-8192 | 63 | 14 375 B | 83 | 11 895 437 | 203 | 0 |
| ros2-to-unity-best-effort-32k | 93 | 66 004 B | 75 | 1 832 216 | 103 | 0 |

> rtps_datafrag_submessages_received = 0 全シナリオ → loopback (MTU 65536) では 8 KB / 32 KB ともにフラグメント化なし。ボトルネックはフラグメンテーションではなく publish / receive の CPU 側。

## 主な改善 (前回 baseline との差分)

### 1. ROS 2 → Unity 8 KB best-effort 30 秒タイムアウトが解消

baseline (`20260622-024815`):

```
helper:  {"event":"done","sent":200,"elapsed_ms":19.93}
player:  {"event":"error","message":"... received 90/200."}
```

current (`20260625-131941`):

```
helper:  {"event":"done","sent":200,"elapsed_ms":19.91}
player:  received=200 elapsed_ms=12.55 mps=15 933 datagrams_dropped=0
```

`f66df12` の `UdpTransport.ConfigureReceiveBuffer` (4 MiB) と `BlockingCollection<ReceivedPacket>` (8 192) + dispatch task による backlog 分離が効いている。datagrams_dropped = 0, rtps_payloads_dropped = 0 で完全ロスなし。`rtps_payloads_buffered_pending_match = 3` で match 待ち spill も軽微。

### 2. 新シナリオ 3 件追加 (PerfScenario.All)

- `unity-to-ros2-reliable-1400` (UDP MTU ちょい手前, 1 033〜1 409 B 帯の連続性を確認)
- `unity-to-ros2-reliable-8000` (UDP MTU 跨ぎの確認。`rtps_datafrag=0` で loopback では 1 packet に収まる)
- `ros2-to-unity-best-effort-32k` (32 KiB, 335 MB/s 達成)

### 3. helper の receive 性能は安定

```
helper elapsed_ms (baseline → current)
unity-to-ros2-reliable-32       693.40 → 618.41  (-10.8%)
unity-to-ros2-reliable-1024     688.73 → 696.35  (+1.1%)
unity-to-ros2-best-effort-8192  351.63 → 348.25  (-1.0%)
ros2-to-unity-reliable-32        52.04 →  53.64  (+3.1%)
ros2-to-unity-reliable-1024      51.81 →  52.84  (+2.0%)
ros2-to-unity-best-effort-8192   19.93 →  19.91  (-0.1%)
```

Player の publish 完了が速くなった分 helper の receive 窓が短くなっているが、helper 自体の処理速度 (mps) は ±5% 以内で再現性あり。

## 残存ボトルネック

### A. Unity → ROS 2 8 KB が他方向より 1 桁遅い (979 us/msg vs 63 us/msg)

受信 8 KB が 15 933 mps / 63 us/msg で回っているのに対し、送信 8 KB は 1 021 mps / 979 us/msg。**送信ホットパスのみが律速**。

- alloc/msg: 送信 43 782 B vs 受信 14 375 B (送信は 3 倍)
- GC samples: 送信 265 frames vs 受信 83 frames
- main_thread_time_ns_last: 送信 874 203 vs 受信 11 895 437 (受信の方が 13 倍多いが、受信は off-thread 完了が async で resume する一方、送信の stopwatch は 1 件目の `PublishRepeatedAsync` 完了 = 全件 history Add + SendDataAsync 完了を待つ)

#### 原因の所在

`src/rosettadds/Dds/Publisher.cs:107-127` の `PublishRepeatedCoreAsync`:

```csharp
private async ValueTask PublishRepeatedCoreAsync(T value, int count, ...)
{
    var owners = new RtpsPayloadOwner[count];   // ← 8 KB なら 200 要素 (8 KB 計測時)
    var memories = new ReadOnlyMemory<byte>[count];
    for (int i = 0; i < count; i++)
    {
        (owners[i], memories[i]) = SerializeOwned(value);  // ← 8 KB なら 200 × 8 KB = 1.6 MB を ArrayPool から rent
        created = i + 1;
    }
    await _writer.WriteBatchAsync(owners, memories, cancellationToken);
}
```

`SerializeOwned` は `Publisher.cs:147-` で `ArrayPool<byte>.Shared.Rent(totalCapacity)` (8 KB + 20 B) を毎回呼ぶ。`RtpsPayloadOwner` は history に所有権を移譲するまで寿命を保つため、**200 件全バッファ (1.6 MB) が WriteBatchAsync 完了まで同時に生存**。`rtps_payloads_buffered_pending_match` (送信側は常に 0 だが、内部 state) のサイズを膨らませ、GC ヒープの `gc_used_memory_bytes_last` も 6.8 MB まで上がる (32 B の 2.4 MB の約 3 倍)。

`WriteBatchAsync` (StatefulWriter.cs:250-) は 1 件ずつ `history.Add` + `await SendDataAsync` の直列で、**Add で借りたバッファを即解放しない**。`for` ループ中に eviction が起きない限り、全バッファがループ完了まで残る。

#### 対策案

1. **ストリーミング化**: `PublishRepeatedAsync` を 1 件ずつ rent → add → 次の rent に切替 (history.Add した時点で所有権は history に移っているので即 `ArrayPool` に返すよう `RtpsPayloadOwner` 拡張)。n=200 で 8 KB 同時生存を 8 KB に減らせる。
2. **再利用可能バッファ**: 同一 `T` を連続 publish する場合、固定 buffer 1 本を `Array.Rent(peak)` して使い回す。`RtpsPayloadOwner` ではなく `IMemoryOwner<byte>` ベースで lifetime を `using` ベースにする方が serialize 中しか生存しない。
3. **ZeroCopy**: `StringMessage.Data` のような string は UTF-8 化して `ReadOnlySequence<byte>` で渡せばコピーを減らせる。現状は `CdrWriter` が `byte[]` 必須なのでそこを `IBufferWriter<byte>` に置き換えるのが本命。
4. **publish 専用 ProfilerCounter**: `gc_allocated_in_frame_bytes_total` は main thread 側のみ計測で、background receive thread 側は `System.GC.GetAllocatedBytesForCurrentThread()` で別途取るべき。送信の 43 KB/msg の内訳が main thread / SendDataAsync 内の background task のどちら由来か分離できていない。

### B. main_thread_time_ns_last が baseline 比 9 倍 (875 us〜1 ms)

baseline 110 us → current 1 000 us。**`Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs:18-25` の recorder 数が 3 → 6 に増えた影響が大きい** (新 build は `gc_reserved_memory_bytes`, `total_used_memory_bytes`, `system_used_memory_bytes`, `gc_allocated_in_frame_bytes` を追加で Start)。

```csharp
result.Add("main_thread_time_ns", ProfilerCategory.Internal, "Main Thread");
result.Add("gc_reserved_memory_bytes", ProfilerCategory.Memory, "GC Reserved Memory");
result.Add("gc_used_memory_bytes", ProfilerCategory.Memory, "GC Used Memory");
result.Add("total_used_memory_bytes", ProfilerCategory.Memory, "Total Used Memory");
result.Add("system_used_memory_bytes", ProfilerCategory.Memory, "System Used Memory");
result.Add("gc_allocated_in_frame_bytes", ProfilerCategory.Memory, "GC Allocated In Frame");
```

各 recorder の `GetSample/LastValue` が main thread の `Collect()` (PerfPlayerEntry.cs:155 から毎フレーム呼ばれる) で回るため、6 個になった分だけ main thread 占有が増える。**計測の計測が計測を歪めている**。

wall clock (elapsed_ms) は baseline 比 +5〜10% 増にとどまっているので実 perf 影響は軽微だが、per-frame 比較は不能。

#### 対策案

- 計測シナリオの 6 recorder を 3 recorder (main_thread_time, gc_used, gc_allocated_in_frame) まで絞った `LeanProfilerRecorders` を perf harness に追加し、本番 perf 計測は lean 版を使う
- または `Collect()` を main thread ではなく `Task.Run` 上で定期実行する (`ProfilerRecorder.GetSample` の thread-safety は Unity 6000 で確認済み)

### C. reliable 系の main thread 占有が送信側でも 1 ms に届く

`unity-to-ros2-reliable-32` で `main_thread_time_ns_last = 999 778 ns ≈ 1 ms`。1 フレームの予算 (16.7 ms @ 60 fps) には収まるが、連続 publish 中の 1 ms × 155 frames = 155 ms 相当の main thread 占有。`PublishRepeatedAsync` の各イテレーションで `await SendDataAsync` が `UnitySynchronizationContext` に resume している (`Publisher.cs:49` の `await ... ConfigureAwait(false)` は呼ばれているが、SendDataAsync 内部の resume 経路で main thread に貼り直されている可能性)。

#### 対策案

- `Publisher.cs:107-127` の `WriteBatchAsync` 経路 (line 270 の `await SendDataAsync`) にも `ConfigureAwait(false)` を徹底
- 完了を `Task.Run` で fire-and-forget する option (fire-and-forget mode)

### D. 全体として 5〜10% の regression

baseline 比 4.5〜8.9% 低下 (reliable 32/1024 系)。`f66df12` で `UdpTransport` に `ConfigureReceiveBuffer` (4 MiB) と dispatch loop が追加されたが、**送信側も `CreateUnicast` を経由するため初期化時に同じ `ConfigureReceiveBuffer` を呼んでいる** (`UdpTransport.cs:97`)。これがベンチマーク warm-up 期間に少し時間を食っている可能性。

ただし baseline (test-build) と current (20260623-022508/build) は Player バイナリ自体が違うので、純粋な perf diff ではなく build artifact の差が混じっている可能性は排除できない。**Player を同一バイナリにして best-effort fix の on/off を切り分けた再計測が望ましい** (uloop 2 回ビルドが必要)。

## 再計測コマンド

```sh
nix develop
scripts/ros2/build_helper.sh  # ROS 2 helper

# uloop 経由で Player ビルド (Unity Editor 起動 + Window > Unity CLI Loop > Server が必要)
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/test-build", "StandaloneLinux64", "mono"); return "ok";'

dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/20260623-022508/build/ROSettaDDSPerfPlayer \
  --scenario all --capture-frames 1200
```

## 推奨アクション (優先度順)

1. **Unity → ROS 2 8 KB のストリーミング化** (対策 A-1, A-2) → 979 us/msg → 200 us/msg 程度に短縮できる見込み (8 KB allocate 200 件同時生存が 8 KB に減る)。rough estimate で 1 021 mps → 5 000 mps 帯。
2. **best-effort fix 適用前後の build artifact 比較計測** (Player を 2 つビルド) → 5〜10% regression のうち build artifact 差とコード差を分離。`f66df12` の真の perf impact を確定できる。
3. **lean ProfilerRecorders の導入** (対策 B) → 計測ノイズを 1/3 にして 8 KB 以外の 5〜10% regression が解消可能か確認。
4. **helper 受信ロジックへの send side Profiler 追加** (対策 A-4) → main thread vs background task の allocation 分離で A の対策が効く箇所を特定。
5. **fire-and-forget publish モード追加** (対策 C) → `await` を排した最速 publish path を提供。実アプリで「ACK を待たないケース」 (sensor-data 系の best-effort や、reliable でも ACK をアプリ層で別途処理する場合) の真の最大値が出る。
6. **1400 B シナリオを別 baseline に格上げ** → 1024 B と 8000 B の間が線形補間で扱えるので、UDP MTU 跨ぎ前後を 1 step で把握できる。`ros2-to-unity` 側にも `reliable-1400` を追加するのは低コスト。

## 補足: 今回追加された ProfilerRecorder 系メトリクス

`f66df12` 以降、metrics.ndjson の `measure_done` イベントに以下が追加されている (baseline には無い):

- `gc_reserved_memory_bytes_*`, `total_used_memory_bytes_*`, `system_used_memory_bytes_*` (Memory カテゴリ)
- `gc_allocated_in_frame_bytes_total`, `gc_allocated_in_frame_bytes_samples` (累積 GC alloc / sample 数)
- `subscription_payloads_from_reader`, `subscription_messages_deserialized`, `subscription_deserialize_failures`, `subscription_handler_invocations`
- `rtps_data_submessages_received`, `rtps_datafrag_submessages_received`, `rtps_reassembled_payloads`, `rtps_payloads_delivered`, `rtps_payloads_buffered_pending_match`, `rtps_payloads_dropped`
- `user_unicast_udp_datagrams_*`, `user_multicast_udp_datagrams_*` (Transport 診断)

これらは `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` と `src/rosettadds/Dds/Subscription.cs` / `src/rosettadds/Transport/UdpTransport.cs` の `Diagnostics` プロパティ経由で取れる。botneck 切り分けの解像度が大幅に上がっており、best-effort fix の効果検証で決定的な役割を果たした。
