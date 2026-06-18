# Unity ROS 2 perf 計測 結果

`ROSettaDDS.UnityRos2Perf.Tests.ROSettaDDSUnityRos2PerfTests.ROS_2_loopback_perf_を記録する` を
Nix dev shell (ROS 2 Humble + Fast DDS, 同一マシン loopback) で走らせた結果。

## 計測条件

- ROS 2 Humble + `rmw_fastrtps_cpp`
- `ROS_LOCALHOST_ONLY=1`
- Unity 6000.3.7f1, Mono runtime
- helper executable は `ROSETTADDS_ROS2_PERF_HELPER` 環境変数で指定
- 6 scenario (3 Unity→ROS2 + 3 ROS2→Unity) を直列に実行
- 各 scenario の計時範囲は steady-state burst のみ (process spawn / discovery / GC は除外):
  1. helper / Unity 側 endpoint 起動 → ready 待ち
  2. matched 確立 (SEDP 完了) を待機
  3. Unity→ROS2 は warmup burst (50 msgs)。ROS2→Unity は helper を `--measure-start` で
     起動し `armed` イベント + stdin 同期で publish 開始を計時開始後に揃える
  4. steady-state burst (scenario.MessageCount msgs) を Stopwatch で計時
  5. throughput / delivery_rate / memory delta を Measure.Custom で sample group に記録

## 結果

| 方向 | QoS | payload | fanout | msgs | elapsed | throughput | bytes/s | delivery_rate |
|------|-----|---------|--------|------|---------|------------|---------|---------------|
| UnityToRos2 | Reliable | 32 B    | 1 sub  | 500 | 593 ms   | 844 msg/s  | 34.6 KB/s | 1.0 |
| UnityToRos2 | Reliable | 1024 B  | 1 sub  | 500 | 586 ms   | 853 msg/s  | 881 KB/s  | 1.0 |
| UnityToRos2 | BestEffort | 8192 B | 2 subs | 200 (x2) | 226 ms | 1769 msg/s | 14.5 MB/s | 1.0 |
| Ros2ToUnity | Reliable | 32 B    | 1 pub  | 500 | 59 ms    | 8514 msg/s | 349 KB/s  | 1.0 |
| Ros2ToUnity | Reliable | 1024 B  | 1 pub  | 500 | 67 ms    | 7518 msg/s | 7.77 MB/s | 1.0 |
| Ros2ToUnity | BestEffort | 8192 B | 2 pubs | 200 (x2) | 20016 ms (timeout) | 20 msg/s | 164 KB/s | 0.46 |

- Reliable scenario はすべて delivery_rate = 1.0 で通過し、`Assert.AreEqual` で厳密一致を要求する
  (loopback + matched 完了後の Reliable で損失は発生しないはず)。
- BestEffort 2-pub scenario は 46% delivery で記録 (timeout 20s 以内に届いた件数)。
  BestEffort QoS 仕様上の損失で、`delivery_rate` sample group に記録するだけで test fail にはしない。
- `serialized_bytes_per_message` は両方向とも
  `CdrEncapsulation.Size + StringMessageSerializer.GetSerializedSize(message)` で算出し 41/1033/8201 bytes。
  計測直前に DDS endpoint を作らないのでテスト本体への副作用なし。

## 残課題

- **matched 待ち API の正攻法化** ([#83](https://github.com/OJII3/ROSettaDDS/issues/83)):
  `WaitForRemote{Reader,Writer}` が `participant.DiscoveryDb.{Reader,Writer}Snapshot` を直接覗き、
  照合用に topic 名を手で mangle している。ROSettaDDS 本体の matched イベント / `WaitForMatchedAsync`
  に置き換えて topic 名 mangle をユーザから隠蔽する。
- **Unity→ROS2 が ROS2→Unity より遅い**: Reliable 32B で 844 vs 8514 msg/s。
  仮説は `PublishAsync().GetAwaiter().GetResult()` の同期ループが律速になっていること。#83 と同時に対処したい。
- **計測の反復化 (median)**: 現状は 1 sample。`Measure.Method` + `WarmupCount` / `MeasurementCount`
  で N 反復の中央値を取る拡張。
- **BestEffort 2-pub の損失**: 46% delivery。Reliable に切り替えた参考値との比較は未取得
  (`delivery_rate` は sample group に記録済み)。

## 計測手順

```sh
# 1. nix develop に入る (or flake.nix が activate した direnv 環境)
direnv allow .

# 2. helper を build
scripts/ros2/build_helper.sh

# 3. Unity をこの dev shell 内で起動 (env 継承のため)
unityhub  # or scripts/unity/run_playmode.sh

# 4. helper の実行パスを export
export ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper"

# 5. テスト実行
scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

結果: `~/.config/unity3d/DefaultCompany/Ros2Unity/PerformanceTestResults.json` に
sample group 単位で記録される。
