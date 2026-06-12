# 互換性と検証方針

rosettadds は ROS 2 と互換な Pure C# RTPS/DDS 通信ライブラリとして整備する。
当面は対応範囲を広げるより、検証済みの範囲を明確にし、変更時に同じ観点を繰り返し確認できることを優先する。

## 対応方針

| 領域 | 現在の扱い | 検証方法 |
| --- | --- | --- |
| .NET | `net8.0` を primary target とする | GitHub Actions の `dotnet build` / `dotnet test` |
| Unity | Unity 6000.3 / .NET Standard 2.1 を検証済み環境とする | `Ros2Unity` の EditMode / PlayMode test と `src/rosettadds` の `netstandard2.1` build |
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
rosettadds の user reader は `DATA` と `DATA_FRAG` のどちらも同じ serialized payload として上位へ渡す。

`DATA_FRAG` は remote writer GUID、writer sequence number、sample size をキーに再構成する。
fragment number は 1 始まりとして扱い、重複 fragment は同じ位置に上書きして安全に無視する。
fragmented sample に `InlineQos` が含まれる場合は、sample 完成時に最後に届いた fragment ではなく、再構成中に受け取った `InlineQos` を使って `STATUS_INFO` を判定する。
未完成 sample は reader 内に保持するが、大容量 topic でメモリを食い潰さないよう、sample size 上限、同時再構成数、TTL による破棄を reader の責務にする。

user endpoint の EntityId は topic hash ではなく participant 内の連番 allocator で割り当てる。
reader は `0x00000504`、writer は `0x00000503` から始め、以後 endpoint 作成ごとに entityKey を増やす。
topic hash は SEDP 以前の簡易 matching には便利だったが、Fast DDS などの ROS 2 実装が user endpoint として採用しない形を作りやすいため、互換性の基準にはしない。

受信経路は user multicast と user unicast の両方を reader に流す。
remote writer の advertised locator に関わらず、reader 側では届いた RTPS packet を entity id / writer GUID でフィルタする。
これにより Fast DDS が user multicast port に画像 fragment を送る場合と、unicast locator に DATA を送る場合の両方を同じ subscription で扱う。

## QoS と history depth の現状

Publisher は `ReliabilityQos` を SEDP の advertised QoS として公開するが、送信実体は現時点では常に
`StatefulWriter` を使う。`ReliabilityQos.BestEffort` は remote endpoint matching 用の metadata として扱い、
Best Effort 専用の `StatelessWriter` 経路へ切り替える API はまだ持たない。

`CreatePublisher` は `DurabilityQos` を受け取る。`DurabilityQos.TransientLocal` を指定すると
`StatefulWriter(resendHistoryOnMatch: true)` へ配線され、後発でマッチした reader に history を再送する
(`/tf_static` 相当)。既定は `Volatile`。

`CreateSubscription` は `ReliabilityQos` を受け取る (既定は ROS 2 と同じ `Reliable`)。
`Reliable` 指定時は HEARTBEAT に ACKNACK を返す `StatefulReader` 経路へ、`BestEffort` 指定時は
`StatelessReader` 経路へ接続する。

Publisher の writer history depth と HEARTBEAT 周期は `DomainParticipantOptions` の
`UserWriterHistoryDepth` (既定 1000) / `UserWriterHeartbeatPeriod` (既定 1 秒) で調整する。

> Volatile reader への late-join 抑止は未実装。Volatile publisher は新規サンプルを reader に積極再送しないが、
> Reliable reader が HEARTBEAT に対し未受信 SN を NACK した場合、join 前のサンプルが再送され得る
> (GAP による pre-join sample の明示的除外は今後の課題)。

## RTPS Receiver の集約

受信経路は participant 単位で 1 つの `ParticipantRtpsReceiver` に集約する。
全 transport (metatraffic / user の multicast・unicast) の受信を 1 経路で受け、パケットを 1 度だけ
パースする。各 submessage は宛先 EntityId に応じて該当 endpoint へ fan-out する
(reader 宛で readerEntityId が UNKNOWN の場合は全 reader へブロードキャストし、各 reader が
matched writer で内部フィルタする)。INFO_DST による宛先フィルタと INFO_SRC / INFO_TS の
Receiver 状態引き継ぎも中央で処理する。

## Vendor ID

rosettadds は独自 Vendor ID `0x013F` を使う (`VendorId.ROSettaDDS`)。
以前は eProsima Fast-DDS の `0x010F` を借用していたが、vendorId が eProsima のとき相手実装が
自社拡張 PID の解釈や互換動作を有効化し誤動作するリスクがあったため、OMG 未登録の独自値へ切替えた。
Fast DDS (`rmw_fastrtps_cpp`) との pub/sub・discovery が双方向で成立することを確認済み
(`demo_nodes_cpp` talker/listener と `samples/TalkerListener`)。`GuidPrefix` 先頭 2 バイトにもこの値が入るため、
GUID 空間も eProsima と分離される。

## User data CDR 読み取り上限

Subscription は `DomainParticipantOptions.CdrReadLimits` を使って user data payload をデシリアライズする。
既定値は `DataFragReassemblyOptions` の既定 sample size と同じ 16 MiB に合わせ、`uint8[]` / `ByteMultiArray`
のような 1 byte 要素 sequence が既定の再構成上限まで読めるようにする。より小さい受信上限が必要な用途では
`DomainParticipantOptions.CdrReadLimits` を明示して participant 単位で制限する。

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
- `src/rosettadds` が `netstandard2.1` でも compile できること
- `dotnet pack` で NuGet package として組み立てられること

`netstandard2.1` build は Unity の API 互換性に近い検証だが、Unity Editor の C# compiler 互換性を完全には保証しない。
現在の `src/rosettadds` は file-scoped namespace と global using を使うため、Unity package の最低バージョンは
検証プロジェクトと同じ Unity 6000.3 に合わせる。`src/rosettadds/csc.rsp` は `-langversion:10.0` に固定し、
Unity Editor の compiler 更新で `latest` の意味が変わることを避ける。

Unity 対応を「検証済み」と呼ぶ条件は以下とする。

- `Ros2Unity/ProjectSettings/ProjectVersion.txt` の Unity Editor で package compile が通る。
- `scripts/unity/run_unity_editmode_tests.sh` が成功する。
- `scripts/unity/run_unity_playmode_tests.sh` が成功する。
- Unity 2022.3 など別バージョンへ対応を広げる場合は、先に C# language version と asmdef compile の実検証を追加する。

ROS 2 interop は OS / ROS distribution / RMW / network interface の影響が大きいため、最初はローカルまたは専用 runner で実行する。
CI に入れる場合は、Docker または ROS 2 セットアップ済み runner を使い、通常の unit test とは分離する。

## ROS 2 daemon の扱い

ROS 2 daemon は `ros2 topic list` や `ros2 node list` などの CLI introspection を高速化する graph cache であり、rosettadds と ROS 2 node の pub/sub 通信に必須の中央 daemon ではない。

interop 検証では古い graph cache による誤判定を避けるため、検証前に `ros2 daemon stop` を実行する。
疎通判定は `ros2 topic list` の結果だけに依存せず、実際に message が届いたログで行う。

## Zenoh の扱い

ROS 2 の `rmw_zenoh_cpp` は DDS/RTPS backend ではなく、Zenoh router (`zenohd`) と liveliness token を使う非 DDS RMW として扱われる。
現在の rosettadds は DDSI-RTPS wire compatibility を中核にしているため、Zenoh 対応は既存 RTPS 実装の延長ではなく別 backend として検討する。

そのため、Zenoh は短期の互換性ゲートには入れない。
まず Fast DDS と Cyclone DDS の範囲で ROS 2 topic / type / QoS / discovery の期待値を固める。
