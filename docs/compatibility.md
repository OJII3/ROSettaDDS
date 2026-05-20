# 互換性と検証方針

rclsharp は ROS 2 と互換な Pure C# RTPS/DDS 通信ライブラリとして整備する。
当面は対応範囲を広げるより、検証済みの範囲を明確にし、変更時に同じ観点を繰り返し確認できることを優先する。

## 対応方針

| 領域 | 現在の扱い | 検証方法 |
| --- | --- | --- |
| .NET | `net8.0` を primary target とする | GitHub Actions の `dotnet build` / `dotnet test` |
| Unity | Unity 2022.3 / .NET Standard 2.1 を目標環境とする | まず `src/rclsharp` を `netstandard2.1` でも build する |
| ROS 2 distribution | Humble を最初の interop baseline とする | `scripts/interop/fastdds_talker_listener.sh` |
| RMW | Fast DDS (`rmw_fastrtps_cpp`) を primary target とする | ROS 2 demo node との pub/sub 相互通信 |
| RMW | Cyclone DDS は次の検証候補とする | Fast DDS の interop script を一般化して追加する |
| RMW | Zenoh は research target とする | DDS/RTPS 実装とは別 backend として設計調査する |
| Transport | UDPv4 / multicast / unicast を対象とする | loopback tests と ROS 2 interop |
| Messages | `std_msgs` と `builtin_interfaces` の一部を対象とする | bit-exact fixtures と serializer tests |
| QoS | Reliable / Best Effort の pub/sub を対象とする | unit tests と ROS 2 interop |

## RTPS user data 受信

ROS 2 RMW 実装は MTU を超える serialized payload を `DATA_FRAG` submessage として分割送信する。
たとえば `sensor_msgs/Image` の 640x480 RGB payload は約 921KB になり、Fast DDS では通常の `DATA` ではなく `DATA_FRAG` で届く。
rclsharp の user reader は `DATA` と `DATA_FRAG` のどちらも同じ serialized payload として上位へ渡す。

`DATA_FRAG` は remote writer GUID、writer sequence number、sample size をキーに再構成する。
fragment number は 1 始まりとして扱い、重複 fragment は同じ位置に上書きして安全に無視する。
未完成 sample は reader 内に保持するが、大容量 topic でメモリを食い潰さないよう、sample size 上限、同時再構成数、TTL による破棄を reader の責務にする。

user endpoint の EntityId は topic hash ではなく participant 内の連番 allocator で割り当てる。
reader は `0x00000504`、writer は `0x00000503` から始め、以後 endpoint 作成ごとに entityKey を増やす。
topic hash は SEDP 以前の簡易 matching には便利だったが、Fast DDS などの ROS 2 実装が user endpoint として採用しない形を作りやすいため、互換性の基準にはしない。

受信経路は user multicast と user unicast の両方を reader に流す。
remote writer の advertised locator に関わらず、reader 側では届いた RTPS packet を entity id / writer GUID でフィルタする。
これにより Fast DDS が user multicast port に画像 fragment を送る場合と、unicast locator に DATA を送る場合の両方を同じ subscription で扱う。

## 現時点で対応しないもの

以下はライブラリ設計に影響が大きいため、対応前に別途設計文書と検証方法を用意する。

- services
- actions
- parameters
- lifecycle nodes
- rosbag2 連携
- SROS2 / DDS Security
- IDL / `.msg` / `.srv` からの型生成
- Zenoh backend
- 複数 DDS vendor の完全互換保証

## CI の役割

CI は最初から ROS 2 実機能をすべて検証する場所にはしない。
まず以下を常時ゲートにする。

- `dotnet build` で library / samples が compile できること
- `dotnet test` で unit tests と loopback integration tests が通ること
- `src/rclsharp` が `netstandard2.1` でも compile できること
- `dotnet pack` で NuGet package として組み立てられること

`netstandard2.1` build は Unity の API 互換性に近い検証だが、Unity Editor の C# compiler 互換性を完全には保証しない。
Unity 2022.3 では C# 9 相当の制約があるため、file-scoped namespace や global using など C# 10 以降の構文は別途整理が必要になる。
Unity 対応を「検証済み」と呼ぶのは、Unity Editor 上の package compile または GameCI 等の Unity runner を通した後にする。

ROS 2 interop は OS / ROS distribution / RMW / network interface の影響が大きいため、最初はローカルまたは専用 runner で実行する。
CI に入れる場合は、Docker または ROS 2 セットアップ済み runner を使い、通常の unit test とは分離する。

## ROS 2 daemon の扱い

ROS 2 daemon は `ros2 topic list` や `ros2 node list` などの CLI introspection を高速化する graph cache であり、rclsharp と ROS 2 node の pub/sub 通信に必須の中央 daemon ではない。

interop 検証では古い graph cache による誤判定を避けるため、検証前に `ros2 daemon stop` を実行する。
疎通判定は `ros2 topic list` の結果だけに依存せず、実際に message が届いたログで行う。

## Zenoh の扱い

ROS 2 の `rmw_zenoh_cpp` は DDS/RTPS backend ではなく、Zenoh router (`zenohd`) と liveliness token を使う非 DDS RMW として扱われる。
現在の rclsharp は DDSI-RTPS wire compatibility を中核にしているため、Zenoh 対応は既存 RTPS 実装の延長ではなく別 backend として検討する。

そのため、Zenoh は短期の互換性ゲートには入れない。
まず Fast DDS と Cyclone DDS の範囲で ROS 2 topic / type / QoS / discovery の期待値を固める。
