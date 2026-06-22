# ROSettaDDS perf 改善 followup 検証レポート

## 概要

`docs/superpowers/specs/2026-06-22-perf-revisit-findings.md` で挙げられた 4 つの
推奨アクションが main に取り込まれた状態で期待どおりの改善が出たかを実測で検証
した結果、**アクション 1 と 2 は期待された改善が得られておらず、アクション 3 と 4
は想定どおり機能している**ことがわかった。

## 計測環境

| 項目 | 値 |
| --- | --- |
| 計測日 | 2026-06-22 |
| 実行 commit | c1b7bc1 (main HEAD, #88 マージ後) |
| ベースライン | `artifacts/perf/20260622-024815` (06-22 13:00 頃の 6 シナリオ実行) |
| 新ラン | `artifacts/perf/20260622-092922` (本検証、9 シナリオ実行) |
| Player | Unity 6000.3.7f1 / StandaloneLinux64 / mono |
| 計測ツール | `tools/rosettadds-perf-runner` `--scenario all --capture-frames 1200` |
| 既知の実行バグ | なし (WriteSentinel 位置問題なし、helper exit 0) |

## サマリ表 (1:1 比較)

| シナリオ | ベースライン (mps / ms) | 新ラン (mps / ms) | 差分 | 判定 |
| --- | --- | --- | --- | --- |
| unity-to-ros2-reliable-32 | 6 892 / 72.55 | 6 860 / 72.88 | -0.5 % | **未達** |
| unity-to-ros2-reliable-1024 | 6 628 / 75.44 | 6 388 / 78.27 | -3.6 % | **未達** |
| unity-to-ros2-reliable-1400 | (未計測) | 6 332 / 78.96 | - | 新規シナリオ (143) |
| unity-to-ros2-reliable-8000 | (未計測) | 1 593 / 125.57 | - | 新規シナリオ、fragment 化 |
| unity-to-ros2-best-effort-8192 | 1 579 / 126.68 | 1 397 / 143.12 | -11.5 % | **退行** |
| ros2-to-unity-reliable-32 | 9 869 / 50.66 | 9 498 / 52.64 | -3.8 % | 誤差範囲 |
| ros2-to-unity-reliable-1024 | 11 039 / 45.29 | 13 150 / 38.02 | **+19.1 %** | **改善** (要追加検証) |
| ros2-to-unity-best-effort-8192 | TIMEOUT (90/200) | TIMEOUT (83/200) | ~同 | **未解消** |
| ros2-to-unity-best-effort-32k | (未計測) | TIMEOUT (11/100) | - | 新規シナリオ、最も深刻 |

## 4 アクションの検証

### アクション 1: `Publisher<T>.PublishAsync` を throughput mode に

**仕様 (revisit-findings.md)**: 「fire-and-forget, `ConfigureAwait(false)`」で
1 publish の固定オーバーヘッドを除去し、reliable-32 Unity→ROS 2 の mps を
6 700 → 10 000 以上にする。

**実装の状態**:
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` の `WriteAsync` /
  `SendDataToDestinationAsync` 経路は `ConfigureAwait(false)` 化済み
  (line 192, 201, 226, 371, 374, 376, 419, 440, 449, 462, 507, 513, 530, 555, 561,
  625, 692, 703, 707, 723)。
- `StatefulWriter.BuildDataPacket` は `ArrayPool<byte>.Shared.Rent(SendBufferSize)`
  経由に置き換わっており (line 548-571)、RTPS packet 構築のヒープ割り当ては 0 件。
- ただし `src/rosettadds/Dds/Publisher.cs:62-72` の
  `SerializeWithEncapsulation` は **依然として `var buffer = new byte[totalCapacity];`
  を毎 publish 呼び出している** (totalCapacity = encap 4 + 推定 size + 16)。

**実測結果**: 6 892 → 6 860 mps (約 0.5 % 誤差範囲)。**1.5x 改善の目標未達**。

**根本原因**: 仕様書 `2026-06-22-publisher-hot-path-arraypool.md` は
`SerializeWithEncapsulation` の pool 化を「将来の改善余地 (B 案 / 別 issue)」として
明示的にスコープ外としている。scope 外だったとはいえ、ホットパスの allocation を
1 件 / publish 残したままでは ArrayPool 化の効果が相殺されている。`gc_used_memory_bytes`
も baseline と同水準 (2.6 MB / 3.0 MB) で推移しており、GC 圧は下がっていない。

**次のアクション案**:
- `Publisher.SerializeWithEncapsulation` を `ArrayPool<byte>.Shared.Rent` + 上限
  拡張方式に変更。spec を amend し直すか別 spec を起こす。
- `perf/unity-publisher-bottleneck` ブランチの `publisher hot path の allocation
  / throughput 計測` (commit 432a30e, 4da5ad3, 6d20454) を本流にマージすると、
  1 publish あたりの allocation が計測可能になる。

### アクション 2: ros2-to-unity receive loop の spin-wait / event-driven 化

**仕様**: 「`Task.Delay(2)` poll を `AutoResetEvent` か `Volatile.Read` spin-wait に
置換し、8 KiB best-effort scenario の 30 s timeout を解消する」。

**実装の状態 (要修正)**: commit `c1b7bc1` のメッセージは
> `PerfPlayerEntry.cs:RunRos2ToUnity` の wait loop を `Task.Delay(2)` poll から
> `AutoResetEvent` ベースに置換

と書かれているが、`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:152-159` の実コードは
```csharp
bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
    () => Volatile.Read(ref received) >= args.Messages,
    TimeSpan.FromSeconds(30),
    async delay =>
    {
        recorders.Collect();
        await Task.Delay(delay);  // ← delay は内部で 1ms 単位
    });
```
で、`AsyncReceiveWaiter.cs:12-33` の実装は依然として `Task.Delay` poll
(2ms → 1ms に細分化しただけ)。**`AutoResetEvent` も `Volatile.Read` spin-wait も
実装されていない**。

**実測結果**: ros2-to-unity-best-effort-8192 は依然 30s timeout。受信数
90/200 (baseline) → 83/200 (新ラン) で誤差範囲内、新シナリオ
ros2-to-unity-best-effort-32k も 11/100 で timeout。

**根本原因**:
- 実装は poll 細分化のみで event-driven 化されていないため、30s 制限の早期
  検知は変わらない。`AsyncReceiveWaiter.cs:26-28` の `TimeSpan.FromMilliseconds(1)`
  poll なので最悪 1ms 遅延で検知はされるが、8 KiB フラグメントロス自体が
  根本原因。
- ヘルパ側 (`tools/ros2-perf-helper/src/ros2_perf_helper.cpp:160-170`) は
  21.29ms で 200 メッセージ送信完了。Player 側は 30s かけても 83 個しか受信
  できていない → Unity 側 UDP receive buffer オーバーフロー / fragment 並び
  替え失敗 / main thread poll では間に合わない、のいずれか。

**次のアクション案**:
- コミットメッセージ (`AutoResetEvent 化`) と実装 (`Task.Delay(1)`) の乖離を
  修正。コードコメントか別 commit で `1ms poll` 化と明記。
- 8 KiB best-effort 問題は receive loop 単独では解消困難。`/proc/sys/net/core/rmem_default`
  の OS デフォルト値確認 + 受信側 UDP buffer の `Socket.ReceiveBufferSize`
  設定見直し、fragment 並べ替え (`UdpTransport` 側) の処理時間計測が必要。
- 32 KiB scenario は helper の送信量 100 個・Player 受信 11 個で 89 個消失。
  これは receive loop 以前の問題。`SendDataAsync` の fragment 分割が単一の
  scratch buffer を 1 個ずつ書き直す実装 (StatefulWriter.cs:603-) で sequential
  await しているため、helper 側 fragment 送信と timing が合っていない可能性。

### アクション 3: fragment 依存 scenario (1400 / 8000 / 32 KiB) 追加

**実装の状態**: `tools/rosettadds-perf-runner/PerfScenario.cs:30-39` の `All` に 3 件
追加済み。runner は 9 scenario を順に実行し、`unity-to-ros2-reliable-1400` /
`unity-to-ros2-reliable-8000` / `ros2-to-unity-best-effort-32k` の 3 つの
metrics ディレクトリが新ランに生成されている。

**実測結果**:
- 1400 B scenario: 6 332 mps (1024 B の 6 388 とほぼ同じ)
- 8000 B scenario: 1 593 mps (reliable + 確実な fragment) → helper exit 3
  (helper が `received=0, elapsed_ms=15000.8` でタイムアウト) は Player 側の
  UDP receive が間に合っていないことを示唆
- 32 KiB scenario: 受信 11/100 で timeout

**判定**: シナリオ追加は完了。効果として、MTU 境界 (1400 / 8000) での
fragment 起因の遅延を別々に計測可能になった。ただし 8000 / 32 KiB は helper /
Player 双方の挙動を追加で追う必要あり。

### アクション 4: ProfilerRecorder に allocation rate 計測追加

**実装の状態**: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs:24,42-44,63` で
`total_used_memory_bytes` (Memory.Total Used Memory) と
`gc_allocated_in_frame_bytes` (Memory.GC Allocated In Frame) を ProfilerRecorder
に積み、`Snapshot()` で last / total / samples を metrics に出力する実装が
入っている。

**実測結果**: 9 シナリオ全てで新メトリクスが記録されている:
- `gc_allocated_in_frame_bytes_total`: 累積 alloc バイト数 (例: reliable-32
  unity-to-ros2 で 4.4 MB / 2506 samples)
- `gc_allocated_in_frame_bytes_samples`: 取得できた frame 数
- `total_used_memory_bytes_last`: 114〜117 MB で安定 (Player 全体)

**判定**: 完了。allocation rate 解析が post-process で可能。

例: unity-to-ros2-reliable-32 の `total=4,427,444 B / 2,506 samples` =
**約 1.77 KB / frame** の allocation rate。`elapsed_ms=72.88` なので
≈ 24 KB / ms = 24 MB / sec 程度の GC 圧。これが PublishAsync 経路の
`new byte[]` 由来と推定。

## 追加で発見した挙動

### ros2-to-unity-reliable-1024 が +19 % 改善

11 039 → 13 150 mps。実装変更点から直接説明できない。run-to-run variance としては
大きすぎる (通常 ±5 %) ので、Unity Editor / OS 状態依存の可能性がある。3 回
ランで再現するか確認する価値あり。

### unity-to-ros2-reliable-8000 で helper exit 3

`helper.stdout.ndjson`:
```
{"event":"ready","mode":"sub","topic":"/rosettadds_perf_unity_to_ros2_reliable_8000"}
{"event":"done","received":0,"elapsed_ms":15000.8}
```
15s 待っても 1 個も受信できていない。Player 側 metrics は `sent=200, mps=1593` で
送信完了している。Unity → ROS 2 方向の 8 KiB reliable フラグメントを
ROS 2 (Fast DDS) 側で reassemble しそこねている可能性。`rmw_fastrtps_cpp`
の UDP receive buffer / fragment timeout 設定の追加計測が必要。

## 残課題 (優先度順)

1. **アクション 1 続き**: `Publisher.SerializeWithEncapsulation` の pool 化 (B 案)。
   これが完了しないと 1.5x 改善目標は未達。
2. **アクション 2 続き**: ros2-to-unity の 8 KiB / 32 KiB fragment 受信失敗の根本
   原因特定 (UDP buffer / receive loop 改良 / fragment 並び替えのいずれか)。
3. **アクション 2 コミットメッセージ修正**: c1b7bc1 の commit message は
   `AutoResetEvent 化` と書かれているが実装は `Task.Delay(1)` 化なので、
   `git commit --amend` か別 commit で実態に合わせる。
4. **8 KiB reliable unity-to-ros2 の ROS 2 側 receive 失敗** (helper exit 3) の
   原因特定。
5. **`perf/unity-publisher-bottleneck` ブランチの allocation / throughput テスト
   (432a30e, 4da5ad3, 6d20454) を main にマージ**し、ホットパスの allocation を
   計測可能な状態にする。

## 再現コマンド

```sh
nix develop
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/test-build", "StandaloneLinux64", "mono"); return "ok";'
# ↑ の出力は OK だが実 build は Ros2Unity/ 配下に出る (path は --project-path 相対)
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build Ros2Unity/artifacts/perf/test-build --scenario all --capture-frames 1200
```
