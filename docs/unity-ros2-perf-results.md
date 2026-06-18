# Unity ROS 2 perf 計測 結果

`ROSettaDDS.UnityRos2Perf.Tests.ROSettaDDSUnityRos2PerfTests.ROS_2_loopback_perf_を記録する` を
Nix dev shell (ROS 2 Humble + Fast DDS, 同一マシン loopback) で 1 回走らせた結果。

## 計測条件

- ROS 2 Humble + `rmw_fastrtps_cpp`
- `ROS_LOCALHOST_ONLY=1`
- Unity 6000.3.7f1, Mono runtime
- helper executable: `tools/ros2-perf-helper/install/.../ros2_perf_helper`
- 6 scenario (3 Unity→ROS2 + 3 ROS2→Unity) を直列に実行
- 各 scenario: 単発 sample (ウォームアップなし、コールドスタート)

## 結果

| 方向 | QoS | payload | fanout | msgs | elapsed | throughput | bytes/s |
|------|-----|---------|--------|------|---------|------------|---------|
| UnityToRos2 | Reliable | 32 B    | 1 sub  | 500 | 589 ms   | 848 msg/s  | 34.8 KB/s |
| UnityToRos2 | Reliable | 1024 B  | 1 sub  | 500 | 587 ms   | 852 msg/s  | 880 KB/s |
| UnityToRos2 | BestEffort | 8192 B | 2 subs | 200 (x2) | 239 ms | 1671 msg/s | 13.7 MB/s |
| Ros2ToUnity | Reliable | 32 B    | 1 pub  | 500 | 383 ms   | 1306 msg/s | 53.5 KB/s |
| Ros2ToUnity | Reliable | 1024 B  | 1 pub  | 500 | 385 ms   | 1297 msg/s | 1.34 MB/s |
| Ros2ToUnity | BestEffort | 8192 B | 2 pubs | 200 (x2) | 20458 ms (timeout) | 19 msg/s | 160 KB/s |

BestEffort 2-pub scenario は 400 送信中 385 受信 (96%) で `Assert.AreEqual` が失敗する。
これは BestEffort QoS 上の実損失。

## 主なボトルネック (修正済み)

1. **SEDP discovery race (修正済み)**: helper の "ready" イベント直後に Unity が publish / receive 判定を始めると、
   endpoint discovery (SPDP + SEDP) が完了する前なので最初の数 10 ~ 400 メッセージが消失していた。
   修正: `DiscoveryDb.{Reader,Writer}Snapshot` の topic 出現を polling する `WaitForRemote{Reader,Writer}` を追加。
2. **pub モードの discovery race (修正済み)**: helper の pub モードは publisher 作成直後に publish ループに入る。
   subscriber が SEDP で発見される前に publish されたメッセージは Reliable QoS でも subscriber が見つからないと
   ack 待ちでスタックする。修正: `publisher->get_subscription_count() > 0` になるまで spin_some を回す pre-publish wait を追加。
3. **dds topic 名組み立てバグ (修正済み)**: `Rcl.Naming.TopicNameMangler.TopicPrefix + topic` で
   `topic = "/foo"` だと `"rt//foo"` の二重スラッシュになり DiscoveryDb の TopicName と一致しない。
   修正: `topic.TrimStart('/')` を挟む。

## 残ボトルネック (未修正)

- **BestEffort 2-pub の 4% 損失**: 96% の到達率。BestEffort の仕様上の損失だが、本来 Reliable と比較すると
  期待値 (`expected = count * fanout`) の assert が厳しすぎる。BestEffort は「ベストエフォート」なので
  アサートは「95% 以上」のように緩めるか、BestEffort の sample group だけ別メトリクスに分離する必要がある。
- **mono used 増加**: BestEffort 8192 B シナリオで 2.8 MB 増加。ヒープ割り当てが定常的に発生している可能性。
  ただし単発 sample のため GC タイミングに依存している。
- **Unity→ROS2 throughput が ROS2→Unity より低い**: Reliable 1-fanout で 850 vs 1300 msg/s。
  Unity 側 publisher の `PublishAsync().GetAwaiter().GetResult()` が同期しているため、
  writer ヒストリが一杯になると publish が詰まる可能性がある。要 profiling。

## 残作業

- [ ] BestEffort scenario の assert 方針を見直し (95% 許容 or 別メトリクス化)
- [ ] mono used の GC 影響を分離するため、各 scenario を 3 回ループして min/median を取る
- [ ] `tools/ros2-perf-helper` への pre-publish wait 追加を PR 化
- [ ] `Ros2PerfHelperProcess` への env 伝播・Humble ros-env 選択を PR 化
- [ ] `ROSettaDDSUnityRos2PerfTests` への SEDP wait + topic 名 fix を PR 化
- [ ] performance test result を CI で取得する仕組みの追加 (現状は手動)
