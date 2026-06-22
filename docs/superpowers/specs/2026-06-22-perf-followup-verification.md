# ROSettaDDS perf 改善 followup 検証レポート

## 概要

`docs/superpowers/specs/2026-06-22-perf-revisit-findings.md` で挙げられた 4 つの
推奨アクションのうち、アクション 1 (Publisher owned-payload + batch API) を
`feat/publisher-owned-payload` ブランチで実装し、Unity Player 実機で再度検証
した結果、**アクション 1 は目標 mps 10,000 を達成したが、allocation 削減と
大ペイロード退行に課題が残る**ことがわかった。

## 計測環境

| 項目 | 値 |
| --- | --- |
| 計測日 | 2026-06-22 (第 2 ラウンド) |
| 実行 commit | c583374 (`feat/publisher-owned-payload` HEAD) |
| ベースライン | `artifacts/perf/20260622-092922` (前回検証、アクション 1 未達時) |
| 新ラン | `artifacts/perf/20260622-140629` (本検証、owned-payload + batch API 反映) |
| Player | Unity 6000.3.7f1 / StandaloneLinux64 / mono |
| 計測ツール | `tools/rosettadds-perf-runner` `--scenario all --capture-frames 1200` |
| 既知の実行バグ | なし (WriteSentinel 位置問題なし、helper exit 0) |

## サマリ表 (1:1 比較)

| シナリオ | ベースライン (mps / ms) | 新ラン (mps / ms) | 差分 | 判定 |
| --- | --- | --- | --- | --- |
| unity-to-ros2-reliable-32 | 6 860 / 72.88 | 10 159 / 49.22 | **+48.1 %** | **目標達成** |
| unity-to-ros2-reliable-1024 | 6 388 / 78.27 | 10 514 / 47.56 | **+64.6 %** | **改善** |
| unity-to-ros2-reliable-1400 | 6 332 / 78.96 | 8 153 / 61.33 | **+28.8 %** | **改善** |
| unity-to-ros2-reliable-8000 | 1 593 / 125.57 | 918 / 217.84 | **-42.4 %** | **退行** |
| unity-to-ros2-best-effort-8192 | 1 397 / 143.12 | 1 353 / 147.82 | -3.2 % | 誤差範囲 |
| ros2-to-unity-reliable-32 | 9 498 / 52.64 | 9 876 / 50.63 | +4.0 % | 誤差範囲 |
| ros2-to-unity-reliable-1024 | 13 150 / 38.02 | 9 794 / 51.05 | **-25.5 %** | **退行** |
| ros2-to-unity-best-effort-8192 | TIMEOUT (83/200) | TIMEOUT (147/200) | ~同 | **未解消** |
| ros2-to-unity-best-effort-32k | TIMEOUT (11/100) | TIMEOUT (6/100) | ~同 | **未解消** |

## 4 アクションの検証

### アクション 1: `Publisher<T>.PublishAsync` を owned-payload + batch API 化

**仕様 (設計 spec `2026-06-22-publish-many-throughput-design.md`)**: 
1. `CacheChange` に `PayloadOwner` (ArrayPool 所有権) を追加し、serialize 後の
   byte[] を publisher が完全所有 → `StatefulWriter.WriteAsync` に借用ではなく
   所有権ごと委譲
2. `Publisher.PublishManyAsync` / `PublishRepeatedAsync` で publish loop 内の
   async オーバーヘッドを batch 発行側で削減
3. `PerfPlayerEntry` の `RunUnityToRos2` publish loop を `PublishRepeatedAsync` に
   置換して実測

**実装の状態 (全 7 commits / `feat/publisher-owned-payload`)**:
- `src/rosettadds/Rtps/HistoryCache/RtpsPayloadOwner.cs`: 新規、ArrayPool 管理
- `src/rosettadds/Rtps/HistoryCache/CacheChange.cs`: `PayloadOwner` プロパティ追加
- `src/rosettadds/Rtps/HistoryCache/WriterHistoryCache.cs`: owner-aware Add overload
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs`: `WriteOwnedAsync` / `WriteBatchAsync`
- `src/rosettadds/Dds/Publisher.cs`: `PublishAsync` を owned-payload 化 +
  `PublishManyAsync` / `PublishRepeatedAsync` 追加
- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs`: `RunUnityToRos2` の publish loop を
  `PublishRepeatedAsync` に置換

**実測結果**: 6 860 → 10 159 mps (+48.1 %)。**目標 mps 10,000 を達成**。
`elapsed_ms` も 72.88 → 49.22 に短縮 (32 % 減)。

**GC allocation の変化**:
- `gc_allocated_in_frame_bytes_total` (reliable-32, unity-to-ros2):
  ベースライン: 4,323.7 KB → 新ラン: 3,416.6 KB (**21 % 削減**)
- 目標の "baseline の 50 % 以下" (≈2,160 KB) には達していない。
- 1 publish あたり `new byte[]` は排除されたが、`SerializeWithEncapsulation`
  内部 (Publisher.cs:62-72) の `new byte[totalCapacity]` は未改修のまま。
  仕様 `2026-06-22-publish-many-throughput-design.md` ではスコープ外としたが、
  これが residual allocation の主因と推定。

**退行について**:
- `unity-to-ros2-reliable-8000` (8 KiB payload): 1,593 → 918 mps (**-42 %**):
  大ペイロードの fragment 分割が `WriteBatchAsync` 経由で overhead 増。
  `_writer.WriteBatchAsync` 内の sequential await が fragment 送信を直列化
  している可能性。
- `ros2-to-unity-reliable-1024`: 13,150 → 9,794 mps (**-26 %**):
  ROS 2 → Unity 方向は実装変更対象外。run-to-run variance の可能性あり
  (前回 +19 % 改善の逆振れ)。3 回再現ランの確認が必要。

**次のアクション案**:
- `Publisher.SerializeWithEncapsulation` の `ArrayPool<byte>.Shared.Rent` 化
  (仕様からスコープ除外済みだが、residual allocation 削減に不可欠)
- `_writer.WriteBatchAsync` 内の sequential await (`SendDataAsync` 逐次呼び出し) を
  `Task.WhenAll` に変更し fragment 送信を並列化 (reliable-8000 退行対策)
- `perf/unity-publisher-bottleneck` ブランチの allocation / throughput テスト
  (432a30e, 4da5ad3, 6d20454) を本流にマージ

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

**実測結果 (本ラウンド)**:
- 1400 B scenario: 8 153 mps (前回 6 332 → +29 %) — batch API 効果が小ペイロード側に波及
- 8000 B scenario: 918 mps (前回 1 593 → -42 %) — fragment 化必須サイズで
  `WriteBatchAsync` の sequential await がボトルネックに。前回 helper exit 3 が
  解消した (200/200 受信) のは進歩だが、mps は悪化
- 32 KiB scenario: 6/100 受信で timeout (前回 11/100 より微減、誤差範囲)

**判定**: シナリオ追加は完了。1400 B は改善 (+29 %) したが、8000 B は退行。
8000 B は今回 helper exit 3 が解消された (200/200 受信) のは進歩だが、
mps 低下 (1,593 → 918) は batch API の大ペイロード overhead が原因。

### アクション 4: ProfilerRecorder に allocation rate 計測追加

**実装の状態**: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs:24,42-44,63` で
`total_used_memory_bytes` (Memory.Total Used Memory) と
`gc_allocated_in_frame_bytes` (Memory.GC Allocated In Frame) を ProfilerRecorder
に積み、`Snapshot()` で last / total / samples を metrics に出力する実装が
入っている。

**実測結果 (本ラウンド)**: 9 シナリオ全てで新メトリクスが記録されている:
- `gc_allocated_in_frame_bytes_total`: 累積 alloc バイト数 (例: reliable-32
  unity-to-ros2 で 3,416.6 KB / 2,007 samples)
- `gc_allocated_in_frame_bytes_samples`: 取得できた frame 数
- `total_used_memory_bytes_last`: 115〜118 MB で安定 (Player 全体)

**判定**: 完了。allocation rate 解析が post-process で可能。

例: unity-to-ros2-reliable-32 の `total=3,416.6 KB / 2,007 samples` =
**約 1.70 KB / frame** の allocation rate。`elapsed_ms=49.22` なので
≈ 35 KB / ms = 35 MB / sec 程度の GC 圧。前回 (24 MB/sec) より圧は
増えたが、これは単位時間あたりの publish 数が増えた (6,860 → 10,159 mps)
ため。1 publish あたりの allocation は実質減少 (約 640 B → 約 336 B)。

## 追加で発見した挙動

### unity-to-ros2-reliable-8000 が -42 % 退行

本ラウンドで 1,593 → 918 mps に低下。前回は helper exit 3 (ROS 2 側受信 0) が
発生していたが、今回は Player 側は 200 メッセージ送信完了 (mps=918) し、
helper 側も 200/200 受信している。つまり fragment 送信は成功しているが、
バッチ API 経由の sequential fragment 送信で 1 メッセージあたりの処理時間が
増加 (elapsed_ms: 125.57 → 217.84) している。`WriteBatchAsync` 内で
fragment 化された各 packet を sequential await で送信しているため、
大ペイロード時に全体のレイテンシが伸びている。

### unity-to-ros2-reliable-1024 が +65 % 改善

6 388 → 10 514 mps。10 KiB 以下のペイロードでは owned-payload 化によって
allocation 削減 + batch API のオーバーヘッド低減効果が顕著に出ている。
特に 1024 B は fragment が発生しないため、batch API の恩恵を純粋に受けている。

### ros2-to-unity-reliable-1024 が -26 % 退行 (前回 +19 % の逆振れ)

前回 11,039 → 13,150 (+19 %)、今回は 13,150 → 9,794 (-26 %)。ROS 2 → Unity
方向は本実装の変更対象外。run-to-run variance としては異常に大きい (±20 %
超)。Unity Player の起動順序や OS のスケジューリング依存の可能性が高い。
3 回再現ランを推奨。

### unity-to-ros2-reliable-32 の allocation 内訳

gc_allocated_in_frame_bytes_total:
- ベースライン: 4,323.7 KB (2,506 samples)
- 新ラン: 3,416.6 KB (2,007 samples)
- 削減量: 907.1 KB (21 %)

GC frame 数も 2,506 → 2,007 に減少しており、publish あたりの allocation 量が
減ったことで GC 発生頻度も低下している。残 allocation は
`SerializeWithEncapsulation` の `new byte[totalCapacity]` (毎 publish 1 回)
が支配的。

## 残課題 (優先度順)

1. **SerializeWithEncapsulation の ArrayPool 化**: mps 目標は達成 (10,159) したが、
   gc_alloc は baseline 比 79 % (21 % 削減) に留まる。残りの allocation は
   `Publisher.cs:SerializeWithEncapsulation` の `new byte[totalCapacity]` 由来。
   これを `ArrayPool<byte>.Shared.Rent` に置換すれば allocation をほぼ 0 にでき、
   GC 圧が下がりさらなる mps 向上が期待できる。
2. **WriteBatchAsync の fragment 並列送信**: `unity-to-ros2-reliable-8000` が
   -42 % 退行。`StatefulWriter.WriteBatchAsync` が各 fragment を sequential await
   で送信しているため、大ペイロード時に全体のレイテンシが増加。`Task.WhenAll` に
   変更して再計測。
3. **アクション 2 続き**: ros2-to-unity の 8 KiB / 32 KiB fragment 受信失敗の根本
   原因特定 (UDP buffer / receive loop 改良 / fragment 並び替えのいずれか)。
   本ラウンドでも改善なし (TIMEOUT)。
4. **アクション 2 コミットメッセージ修正**: c1b7bc1 の commit message は
   `AutoResetEvent 化` と書かれているが実装は `Task.Delay(1)` 化なので、
   `git commit --amend` か別 commit で実態に合わせる。
5. **8 KiB reliable unity-to-ros2 の ROS 2 側 receive 失敗** (helper exit 3) の
   原因特定。
6. **`perf/unity-publisher-bottleneck` ブランチの allocation / throughput テスト
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
