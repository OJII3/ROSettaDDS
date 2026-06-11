# ROS 2 interop 検証

この文書は rosettadds と ROS 2 node の相互運用を確認するための手順を定義する。
unit tests は wire format や loopback の正しさを確認し、interop scripts は実際の ROS 2 RMW 実装と通信できることを確認する。

## 前提

- ROS 2 Humble が利用できること
- `demo_nodes_cpp` が利用できること
- `dotnet` SDK 8.0 が利用できること
- 初期対象 RMW は `rmw_fastrtps_cpp`

Nix devShell を使う場合:

```sh
nix develop
```

## Fast DDS talker/listener

Fast DDS baseline は次のスクリプトで確認する。

```sh
scripts/interop/fastdds_talker_listener.sh
```

Nix devShell では `ros2` CLI の Python extension が macOS の動的ライブラリ解決で失敗する場合があるため、
スクリプトは `$AMENT_PREFIX_PATH/lib/demo_nodes_cpp/{talker,listener}` が存在すれば `ros2 run` を経由せず直接実行する。

このスクリプトは以下を確認する。

- rosettadds publisher から ROS 2 `demo_nodes_cpp listener` に `std_msgs/msg/String` が届くこと
- ROS 2 `demo_nodes_cpp talker` から rosettadds subscriber に `std_msgs/msg/String` が届くこと
- `ROS_LOCALHOST_ONLY=1` とスクリプトが選んだ `ROS_DOMAIN_ID` で loopback 通信できること
- 検証前に `ros2 daemon stop` して古い graph cache の影響を避けること

## Fast DDS large payload

Fast DDS が `DATA_FRAG` を使うサイズの payload は次のスクリプトで確認する。

```sh
scripts/interop/fastdds_large_string.sh
```

このスクリプトは ROS 2 CLI から 32KB 超の `std_msgs/msg/String` を publish し、rosettadds listener が実 payload を受信できることを確認する。
`ros2 topic pub` を使うため、`ros2` CLI が動作する Linux または ROS 2 セットアップ済み環境で実行する。

## 判定基準

interop の成功条件は introspection 結果ではなく、実際の受信ログにする。

- ROS 2 listener 側で `Hello rosettadds` を受信している
- rosettadds listener 側で `Hello World` または `Hello rosettadds` を受信している
- large payload 検証では rosettadds listener 側で `large-payload-` を含む message を受信している

## 次に追加する検証

- Best Effort publisher/subscriber の組み合わせ
- Cyclone DDS (`rmw_cyclonedds_cpp`)
- `std_msgs/Header` など string 以外の msg
- discovery unregister の反映
- Linux CI または self-hosted runner での定期実行
