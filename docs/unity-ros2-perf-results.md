# Unity ROS 2 perf 計測 結果

`ROSettaDDS.UnityRos2Perf.Tests.ROSettaDDSUnityRos2PerfTests.ROS_2_loopback_perf_を記録する` を
Nix dev shell (ROS 2 Humble + Fast DDS, 同一マシン loopback) で 1 回走らせた結果。

## 計測条件

- ROS 2 Humble + `rmw_fastrtps_cpp`
- `ROS_LOCALHOST_ONLY=1`
- Unity 6000.3.7f1, Mono runtime
- helper executable: `tools/ros2-perf-helper/install/.../ros2_perf_helper` (`ROSETTADDS_ROS2_PERF_HELPER` 環境変数で指定)
- 6 scenario (3 Unity→ROS2 + 3 ROS2→Unity) を直列に実行
- 各 scenario:
  1. helper / Unity 側 endpoint 起動 → ready 待ち
  2. matched 確立 (SEDP 完了)
  3. warmup burst (50 msgs, Unity→ROS2 のみ)
  4. steady-state burst (scenario.MessageCount msgs) を Stopwatch で計時
  5. delivery_rate / memory delta を Measure.Custom で sample group に記録

## 結果

| 方向 | QoS | payload | fanout | msgs | elapsed | throughput | bytes/s | delivery_rate |
|------|-----|---------|--------|------|---------|------------|---------|---------------|
| UnityToRos2 | Reliable | 32 B    | 1 sub  | 500 | 580 ms   | 861 msg/s  | 35.3 KB/s | 1.0 |
| UnityToRos2 | Reliable | 1024 B  | 1 sub  | 500 | 580 ms   | 862 msg/s  | 890 KB/s  | 1.0 |
| UnityToRos2 | BestEffort | 8192 B | 2 subs | 200 (x2) | 224 ms | 1783 msg/s | 14.6 MB/s | 1.0 |
| Ros2ToUnity | Reliable | 32 B    | 1 pub  | 500 | 332 ms   | 1508 msg/s | 61.8 KB/s | 1.0 |
| Ros2ToUnity | Reliable | 1024 B  | 1 pub  | 500 | 330 ms   | 1514 msg/s | 1.56 MB/s | 1.0 |
| Ros2ToUnity | BestEffort | 8192 B | 2 pubs | 200 (x2) | 20206 ms (timeout) | 20 msg/s | 162 KB/s | 0.43 |

- Reliable scenarios はすべて delivery_rate = 1.0 で通過。
- BestEffort 2-pub scenario は 43% delivery で記録 (timeout 20s 以内に届いた件数)。
  BestEffort QoS の仕様上の損失で sample group として記録されるだけで test fail にはならない。
- serialized_bytes_per_message は両方向とも `SerializeWithEncapsulation().Length` で算出し 41/1033/8201 bytes。

## レビュー対応サマリ (PR #82)

### #1 環境構築責務を C# から nix devShell/CI へ移譲 (commit fb2e514)

`Ros2PerfHelperProcess` から `ResolveRos2Install` / `IsUsableRosEnv` (librcl.so の ELF grep を含む) を全削除。
ProcessStartInfo は親 env を継承する既定動作のみに。`ROSETTADDS_ROS2_PERF_HELPER` 未設定時は
`InvalidOperationException`。env 未設定なら perf テストは `Assert.Ignore` でスキップし、
nix develop シェルで実行するように案内する。

### #3 steady-state ベンチに作り直し (commit ce16a87)

`matched → warmup → steady-state バーストを Stopwatch` のフローに統一。
process spawn / discovery / GC を計時範囲から除外。
BestEffort は hard fail せず `delivery_rate` sample group として記録。
`serialized_bytes_per_message` の算出を両方向で `SerializeWithEncapsulation().Length` に統一。

### #2 ROSettaDDS 本体に matched 待ち API 追加 (別 issue)

本 PR では対応しない。`DiscoveryDb.{Reader,Writer}Snapshot` を直接 polling する
ワークアラウンド (二重スラッシュバグの温床) は残っているが、
`WaitForRemote{Reader,Writer}` は現状動作しているため別 issue として切り出し。

## 残課題

- **matched API 正攻法化 (PR 範囲外、別 issue)**: `WaitForRemoteReader` 等を
  `participant.DiscoveryDb.{Reader,Writer}Snapshot` の直接覗きから、
  ROSettaDDS 本体の `WaitForMatchedAsync` / matched イベントに置き換え。
  topic 名 mangle をユーザから隠蔽。
- **計測反復 (median)**: 現状 1 sample。`Measure.Method` + `WarmupCount` / `MeasurementCount`
  で N 反復の中央値にする拡張は別 issue。
- **Unity→ROS2 が ROS2→Unity より遅い原因**: 850 vs 1500 msg/s。
  PublishAsync().GetAwaiter().GetResult() の同期ループが主因と疑われるが、
  matched API 化と同時に対処したい。
- **BestEffort 2-pub の損失**: 43% delivery。
  Reliable に切り替えて計測すれば参考値 (53%? 80%?) が出る。`loss_rate` として
  sample group には記録済み。

## 計測手順

```sh
# 1. nix develop に入る (or flake.nix が activate した direnv 環境)
direnv allow .

# 2. helper を build
scripts/ros2/build_helper.sh

# 3. Unity をこの dev shell 内で起動 (env 継承のため)
unityhub  # or scripts/unity/run_playmode.sh

# 4. ROS 2 環境を export
export ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper"

# 5. テスト実行
scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

結果: `~/.config/unity3d/DefaultCompany/Ros2Unity/PerformanceTestResults.json` に
sample group 単位で記録される。
