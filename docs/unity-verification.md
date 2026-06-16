# Unity 検証・計測計画

`Ros2Unity` は rosettadds を Unity Package Manager のローカルパッケージとして読み込み、Unity Editor 上でコンパイル、通信動作、基本的な性能指標を継続確認するための検証プロジェクトとして使う。

## 目的

- Unity 6000.3 系 Editor で `com.ojii3.rosettadds` がパッケージとして解決・コンパイルできることを確認する。
- `DomainParticipant`、`Publisher<T>`、`Subscription<T>` を Unity のテストランナーから起動し、`std_msgs/msg/String` 相当の publish/subscribe が成立することを確認する。
- 通信処理のバッチ時間、messages/sec、serialized bytes/sec、管理ヒープ差分、Unity Profiler のメモリ指標を EditMode テストで記録する。
- 反復 publish/subscribe と create/dispose の後に retained memory を確認し、明らかなリークを EditMode テストで失敗させる。
- 外部 ROS 2 環境や OS の multicast 設定に依存しない計測を先に固定し、外部相互通信は別の手動/CI ジョブで拡張できる形にする。

## 検証範囲

自動検証は用途ごとに 4 層に分ける。

- EditMode は `LoopbackHub` を使う。これは同一プロセス内で RTPS transport の契約を満たすため、Unity Editor のバッチ実行でも安定して通信経路を測れる。
- PlayMode は実 `UdpTransport` を使い、`MonoBehaviour.OnEnable` / `OnDisable` / `OnDestroy` 経由で participant lifecycle を通す。
- Soak は専用の `ROSettaDDS.UnitySoak.Tests` PlayMode アセンブリを使い、通常実行には含めない。
- Player は専用の `ROSettaDDS.UnityPlayer.Tests` PlayMode アセンブリを
  StandaloneOSX Player にビルドし、既定 IL2CPP、切り分け用 Mono で実行する。

計測対象:

- Unity package import: `Ros2Unity/Packages/manifest.json` から `../../src/rosettadds` を参照する。
- Smoke: 2 つの `DomainParticipant` 間で `StringMessage` を複数件送受信し、順序と件数を確認する。
- Throughput: payload サイズ別に warmup 後の publish/subscribe batch を複数回実行し、受信完了までの経過時間、messages/sec、serialized bytes/sec、平均 ms/message を記録する。
- Leak guard: participant / publisher / subscription / transport の create/dispose を繰り返し、full GC 後の managed heap と Unity mono used memory の retained delta を記録し、閾値を超えたら失敗させる。
- Lifecycle smoke: 実 UDP loopback で publish/subscribe し、Play Mode の GameObject disable / destroy 後に participant と background receive loop が停止することを確認する。
- Unity callback contract: subscription handler は background receive thread で呼ばれ、Unity main thread では呼ばれないことを確認する。
- Domain Reload disabled: Enter Play Mode Options で Domain Reload を無効にし、static 状態を保持したまま lifecycle を連続実行できることを確認する。
- Soak: 約 60 秒、50 Hz publish と周期的 create/dispose を継続し、受信継続、retained memory、frame time を確認する。
- AOT Player: 全生成 msg 型を明示的なジェネリック呼び出しで publish/receive し、
  serializer、`Publisher<T>`、`Subscription<T>` の IL2CPP AOT インスタンス化を確認する。

subscription handler は Unity main thread では実行されない。handler 内で
`GameObject` や `Transform` などの Unity API を直接操作せず、受信値を thread-safe な
queue などへ渡して `Update` から反映すること。

`Ros2Unity/ProjectSettings/EditorSettings.asset` は Enter Play Mode Options を有効化し、
Domain Reload を無効にする。PlayMode 停止時には、static 状態が次回実行へ残る前提で
participant / publisher / subscription を必ず dispose する。

対象外:

- 外部 `ros2` CLI / Fast DDS との相互通信。
- 実 NIC や multicast loopback の OS 設定に依存する計測。
- macOS 以外の Player ビルドと端末別プロファイル。

これらは自動検証が安定した後に、別ジョブとして追加する。

## 実行方法

実行スクリプトは、起動中の Unity Editor に uloop (uLoopMCP) で接続できればそれを使い、
接続できなければ Unity Editor を batchmode で起動する。
uloop CLI は nix devshell に含まれ、Unity 側には uLoopMCP パッケージ
(`io.github.hatayama.uloopmcp`) が導入済み。uloop が使えない場合は自動的に
batchmode にフォールバックする。

```sh
scripts/unity/run_editmode.sh
scripts/unity/run_playmode.sh
scripts/unity/run_player_tests.sh
```

通常の `run_playmode.sh` は `ROSettaDDS.UnityPlayMode.Tests` のみを実行する。
60 秒 Soak は明示的に専用アセンブリを指定する。

```sh
scripts/unity/run_playmode.sh \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnitySoak.Tests
```

`run_player_tests.sh` は StandaloneOSX Player をビルドして Player 内でテストを実行する。
既定 backend は IL2CPP。問題の切り分け時のみ Mono を指定する。

```sh
scripts/unity/run_player_tests.sh
scripts/unity/run_player_tests.sh --backend mono
```

Player 実行は batchmode 専用で、起動中 Editor への uloop 接続は使わない。
同じ `Ros2Unity` プロジェクトを Editor で開いている場合は Player 実行前に閉じること。

batchmode を強制する場合 (`Ros2Unity` を開いている Editor は閉じておくこと):

```sh
scripts/unity/run_editmode.sh --batch
```

特定のテストだけ実行する場合:

```sh
scripts/unity/run_editmode.sh --filter-type regex --filter-value 'Loopback_pubsub'
scripts/unity/run_playmode.sh --filter-type assembly --filter-value ROSettaDDS.UnityPlayMode.Tests
```

batchmode 用の Unity Editor は `ProjectSettings/ProjectVersion.txt` のバージョンを基に
Unity Hub の標準パスから自動検出する。完全一致するバージョンが見つからない場合は
同系列 (例: 6000.3.*) の Editor にフォールバックする。明示する場合:

```sh
UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.3.7f1/Unity.app/Contents/MacOS/Unity \
  scripts/unity/run_editmode.sh --batch
```

出力先:

- uloop 実行: `artifacts/unity/uloop-editmode-tests.json` / `artifacts/unity/uloop-playmode-tests.json`
  (テスト件数と pass/fail のサマリ JSON)
- batchmode 実行: `artifacts/unity/editmode-results.xml` + `artifacts/unity/unity-editmode.log` /
  `artifacts/unity/playmode-results.xml` + `artifacts/unity/unity-playmode.log`
- Player 実行: `artifacts/unity/player-<backend>-results.xml` +
  `artifacts/unity/unity-player-<backend>.log` +
  `artifacts/unity/player-<backend>/ROSettaDDSUnityPlayerTests.app`

`artifacts/` は計測結果の生成物なのでコミットしない。

Unity Performance Testing の sample group (throughput / leak guard の計測値) は
batchmode 実行で生成される results XML にのみ埋め込まれる。性能値を確認するときは
`--batch` で実行し、XML を直接参照する。README への性能値の自動反映は行わない。

## ROS 2 performance tests

`ROSettaDDS.UnityRos2Perf.Tests` は Unity PlayMode から ROS 2 C++ helper process を起動し、
同一マシン loopback の Fast DDS 相互通信性能を記録する。通常の PlayMode / Soak には含めず、
明示指定したときだけ実行する。

前提:

- ROS 2 Humble が source 済みであること
- `rmw_fastrtps_cpp` が利用できること
- `tools/ros2-perf-helper` が build 済みであること

helper build:

```sh
cd tools/ros2-perf-helper
colcon build
```

実行:

```sh
ROSETTADDS_ROS2_PERF_HELPER="$PWD/tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper" \
  scripts/unity/run_playmode.sh --batch \
  --filter-type assembly \
  --filter-value ROSettaDDS.UnityRos2Perf.Tests
```

この test は `ROS_LOCALHOST_ONLY=1`、`RMW_IMPLEMENTATION=rmw_fastrtps_cpp`、
scenario ごとの `ROS_DOMAIN_ID` を helper process に設定する。helper が見つからない環境では
`Assert.Ignore` し、通常の Unity 検証を壊さない。

主な sample group:

- `rosettadds.ros2perf.unity_to_ros2.<qos>.<payload>B.subscribers_<n>.elapsed_ms`
- `rosettadds.ros2perf.unity_to_ros2.<qos>.<payload>B.subscribers_<n>.messages_per_second`
- `rosettadds.ros2perf.ros2_to_unity.<qos>.<payload>B.publishers_<n>.elapsed_ms`
- `rosettadds.ros2perf.ros2_to_unity.<qos>.<payload>B.publishers_<n>.messages_per_second`
- `rosettadds.ros2perf.*.managed_heap_delta_bytes`
- `rosettadds.ros2perf.*.unity_mono_used_delta_bytes`

## IL2CPP / AOT 棚卸し

- `ROSettaDDS.UnityPlayer.Tests` は全 32 msg 型を
  `AssertRoundTrip<T>(ICdrSerializer<T>, T)` へ明示的に渡す。これにより各 serializer と
  `Publisher<T>` / `Subscription<T>` の AOT コードを生成し、Player 上で実行確認する。
- ライブラリ内の reflection 使用は
  `DomainParticipant.ResolveDdsTypeName<T>` が各 msg 型の `DdsTypeName` 定数を取得する
  箇所のみ。全 msg 型の Player roundtrip が、この reflection 経路も実行する。
- ROSettaDDS 本体を preserve する `link.xml` は不要。明示ジェネリック参照と
  `DdsTypeName` reflection は IL2CPP Player 上で stripping 後も動作する。
- Player テストアセンブリは NUnit が reflection でテストを発見するため、
  `Assets/Tests/Player/link.xml` で `ROSettaDDS.UnityPlayer.Tests` を preserve する。

## 判定方針

Smoke test は件数・順序・タイムアウトで失敗させる。

Throughput test は現時点では閾値で失敗させず、Unity Performance Testing の sample group に数値を記録する。閾値は複数回のローカル/CI 実測から baseline を作ってから導入する。

Leak guard は throughput と違い、反復後に full GC を挟んだ retained delta に閾値を置く。Unity Editor 自体の一時キャッシュや package 側の初回初期化を避けるため、最初の cycle は warmup として baseline から外す。

初期閾値は managed heap retained delta 8 MiB、Unity mono used retained delta 64 MiB とする。通信速度は 32 B、1024 B、8192 B の `StringMessage.Data` payload で測る。

記録する主な sample group:

- `rosettadds.throughput.<payload>B.elapsed_ms`
- `rosettadds.throughput.<payload>B.messages_per_second`
- `rosettadds.throughput.<payload>B.serialized_bytes_per_second`
- `rosettadds.throughput.<payload>B.mean_message_ms`
- `rosettadds.leak.managed_heap_retained_bytes`
- `rosettadds.leak.unity_mono_used_retained_bytes`
- `rosettadds.leak.unity_total_allocated_delta_bytes`
- `rosettadds.soak.managed_heap_retained_bytes`
- `rosettadds.soak.unity_mono_used_retained_bytes`
- `rosettadds.soak.frame_time_ms`
- `rosettadds.soak.messages_received`
