# Unity ROS 2 performance measurement design

## Goal

Unity 上の ROSettaDDS と ROS 2 Humble / Fast DDS の同一マシン通信について、
再現可能な性能計測を追加する。対象はマシン間通信を除いた loopback 通信で、
Unity 利用時の throughput、受信完了時間、GC / managed heap の傾向を継続して見られる状態にする。

ROSettaDDS の .NET 単体性能ではなく、Unity PlayMode から実 ROS 2 process を起動して測る。

## Scope

初期対象:

- Unity 6000.3 PlayMode tests
- ROS 2 Humble
- `rmw_fastrtps_cpp`
- `ROS_LOCALHOST_ONLY=1`
- 同一マシンの localhost / loopback 通信
- `std_msgs/msg/String`
- Reliable / BestEffort QoS
- payload size: 32 B, 1024 B, 8192 B, 32768 B
- publisher / subscriber fan-out: 1, 2, 4

対象外:

- マシン間通信
- Cyclone DDS など Fast DDS 以外の RMW
- latency の厳密な片道計測
- CI 必須化
- UniTask 化
- ROSettaDDS 本体の最適化

latency は clock 差や helper / Unity 間の計測境界が曖昧になりやすいため、
初期版では throughput と completion time を優先する。latency は後続で RTT 専用シナリオとして追加する。

## Architecture

性能計測は 2 つの層で構成する。

1. `tools/ros2-perf-helper`: ROS 2 C++ helper node
2. `Ros2Unity/Assets/Tests/Ros2Perf`: Unity PlayMode performance tests

Unity test が helper process を起動し、標準出力の JSON Lines を読んで ready / progress / done を同期する。
Unity 側の測定値は Unity Performance Testing の `SampleGroup` に記録する。

通常の `ROSettaDDS.UnityPlayMode.Tests` と 60 秒 soak には混ぜない。
専用アセンブリ `ROSettaDDS.UnityRos2Perf.Tests` を明示指定したときだけ実行する。

## ROS 2 helper

`tools/ros2-perf-helper` は C++ / rclcpp の小さな ROS 2 package とする。
依存は `rclcpp` と `std_msgs` に限定する。

CLI modes:

- `sub`: ROS 2 subscriber として Unity ROSettaDDS publisher から受信する
- `pub`: ROS 2 publisher として Unity ROSettaDDS subscriber へ送信する

主要引数:

- `--mode pub|sub`
- `--topic <name>`
- `--messages <count>`
- `--payload-bytes <bytes>`
- `--rate-hz <rate>`
- `--qos reliable|best_effort`
- `--ready-timeout-ms <ms>`
- `--idle-timeout-ms <ms>`

環境変数:

- `ROS_DOMAIN_ID`: Unity test がシナリオごとに設定する
- `ROS_LOCALHOST_ONLY=1`
- `RMW_IMPLEMENTATION=rmw_fastrtps_cpp`

JSON Lines events:

```json
{"event":"ready","mode":"sub","topic":"/rosettadds_perf_x"}
{"event":"progress","received":1000}
{"event":"done","received":10000,"elapsed_ms":1234.5}
{"event":"error","message":"..."}
```

`pub` mode は publish 完了時に `sent` と `elapsed_ms` を出す。
`sub` mode は期待件数の受信完了、または idle timeout で `done` を出す。

helper の出力は機械読み取りを優先し、人間向けログは stderr に出す。

## Unity test harness

Unity 側には `ROSettaDDS.UnityRos2Perf.Tests.asmdef` を追加し、
`Unity.PerformanceTesting` と `ROSettaDDS` を参照する。

主な責務:

- ROS 2 helper executable の検出
- helper process の起動と停止
- JSON Lines の parsing
- scenario ごとの `ROS_DOMAIN_ID` 採番
- timeout 時の process cleanup
- Unity Performance Testing への metric 記録

helper executable は次の順で解決する。

1. 環境変数 `ROSETTADDS_ROS2_PERF_HELPER`
2. repo 内の標準 build output path

見つからない場合は `Assert.Ignore` とし、通常の Unity 検証を壊さない。
ROS 2 環境がない macOS / Editor でも通常テストは通る必要があるため、perf test は明示実行専用にする。

## Scenarios

### Unity publisher to ROS 2 subscribers

Unity 側で 1 つの `DomainParticipant` と `Publisher<StringMessage>` を作る。
ROS 2 helper `sub` process を 1 / 2 / 4 個起動し、全 helper が `ready` を出してから publish する。

各 payload size と QoS ごとに以下を記録する。

- elapsed ms: publish 開始から全 subscriber 完了まで
- messages/sec: 全 subscriber 受信件数ベース
- serialized bytes/sec
- subscriber count
- timeout count
- Unity managed heap delta
- Unity mono used delta

### ROS 2 publishers to Unity subscriber

Unity 側で 1 つの `DomainParticipant` と `Subscription<StringMessage>` を作る。
ROS 2 helper `pub` process を 1 / 2 / 4 個起動する。
Unity subscriber が期待件数を受信するまで待つ。

各 payload size と QoS ごとに以下を記録する。

- elapsed ms: helper start から Unity 受信完了まで
- messages/sec: Unity 受信件数ベース
- serialized bytes/sec
- publisher count
- timeout count
- Unity managed heap delta
- Unity mono used delta

## Sample groups

命名は `rosettadds.ros2perf.<direction>.<qos>.<payload>B.<fanout>.metric` とする。

例:

- `rosettadds.ros2perf.unity_to_ros2.reliable.1024B.subscribers_4.elapsed_ms`
- `rosettadds.ros2perf.unity_to_ros2.reliable.1024B.subscribers_4.messages_per_second`
- `rosettadds.ros2perf.ros2_to_unity.best_effort.8192B.publishers_2.serialized_bytes_per_second`
- `rosettadds.ros2perf.ros2_to_unity.best_effort.8192B.publishers_2.managed_heap_delta_bytes`

Throughput 系は `increaseIsBetter: true`、時間と memory 系は `increaseIsBetter: false` とする。

## Build and execution

ROS 2 helper は ROS 2 workspace として build する。
想定手順:

```sh
cd tools/ros2-perf-helper
colcon build
```

Unity perf tests:

```sh
ROSETTADDS_ROS2_PERF_HELPER=/path/to/ros2_perf_helper \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

初期実装では helper の自動 build は行わない。
ROS 2 環境の有無により大きく分岐するため、build と test 実行は明示手順に分ける。

## Failure policy

初期版の throughput metrics は閾値で失敗させない。
失敗条件は以下に限定する。

- helper process が起動できる環境なのに `ready` を出さない
- expected message count に到達しない
- helper が `error` event を出す
- process cleanup に失敗する
- Unity 側で未処理例外が出る

性能閾値は複数環境で baseline を取った後に導入する。

## Error handling

Unity test harness は timeout 時に helper の stdout / stderr の末尾を assertion message に含める。
process cleanup は kill 後に wait し、残留 process を残さない。

JSON Lines parser は未知 event を無視しない。未知 event は helper / harness の contract drift として失敗させる。

ROS 2 helper は invalid arguments、QoS 不一致、timeout を JSON `error` event と stderr の両方に出す。

## Testing strategy

TDD で以下を追加する。

- Unity 側 helper JSON parser の単体的テスト
- helper process wrapper の timeout / cleanup テスト
- ROS 2 helper CLI argument validation
- ROS 2 helper の JSON event schema smoke
- Unity PlayMode の明示実行 performance scenario

ROS 2 がない環境では Unity perf scenario は `Assert.Ignore` する。
parser や path resolution など ROS 2 不要の部分は通常 test で実行できる形にする。

## Documentation

`docs/unity-verification.md` に ROS 2 perf test の前提、build 手順、実行手順、出力 sample group を追記する。
`docs/interop.md` には既存の疎通確認と性能計測の違いを追記する。

## Decisions

- ROS 2 helper の package 名は `rosettadds_ros2_perf_helper` とする。
- 初期 message type は `std_msgs/msg/String` のみとする。
- latency は初期対象外とし、RTT scenario の後続設計に回す。
