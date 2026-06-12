# Unity 検証・計測計画

`Ros2Unity` は rosettadds を Unity Package Manager のローカルパッケージとして読み込み、Unity Editor 上でコンパイル、通信動作、基本的な性能指標を継続確認するための検証プロジェクトとして使う。

## 目的

- Unity 6000.3 系 Editor で `com.ojii3.rosettadds` がパッケージとして解決・コンパイルできることを確認する。
- `DomainParticipant`、`Publisher<T>`、`Subscription<T>` を Unity のテストランナーから起動し、`std_msgs/msg/String` 相当の publish/subscribe が成立することを確認する。
- 通信処理のバッチ時間、messages/sec、serialized bytes/sec、管理ヒープ差分、Unity Profiler のメモリ指標を EditMode テストで記録する。
- 反復 publish/subscribe と create/dispose の後に retained memory を確認し、明らかなリークを EditMode テストで失敗させる。
- 外部 ROS 2 環境や OS の multicast 設定に依存しない計測を先に固定し、外部相互通信は別の手動/CI ジョブで拡張できる形にする。

## 検証範囲

自動検証は用途ごとに 2 層に分ける。

- EditMode は `LoopbackHub` を使う。これは同一プロセス内で RTPS transport の契約を満たすため、Unity Editor のバッチ実行でも安定して通信経路を測れる。
- PlayMode は実 `UdpTransport` を使い、`MonoBehaviour.OnEnable` / `OnDisable` / `OnDestroy` 経由で participant lifecycle を通す。

計測対象:

- Unity package import: `Ros2Unity/Packages/manifest.json` から `../../src/rosettadds` を参照する。
- Smoke: 2 つの `DomainParticipant` 間で `StringMessage` を複数件送受信し、順序と件数を確認する。
- Throughput: payload サイズ別に warmup 後の publish/subscribe batch を複数回実行し、受信完了までの経過時間、messages/sec、serialized bytes/sec、平均 ms/message を記録する。
- Leak guard: participant / publisher / subscription / transport の create/dispose を繰り返し、full GC 後の managed heap と Unity mono used memory の retained delta を記録し、閾値を超えたら失敗させる。
- Lifecycle smoke: 実 UDP loopback で publish/subscribe し、Play Mode の GameObject disable / destroy 後に participant と background receive loop が停止することを確認する。

対象外:

- 外部 `ros2` CLI / Fast DDS との相互通信。
- 実 NIC や multicast loopback の OS 設定に依存する計測。
- Player ビルド後の端末別プロファイル。

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
```

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

`artifacts/` は計測結果の生成物なのでコミットしない。

Unity Performance Testing の sample group (throughput / leak guard の計測値) は
batchmode 実行で生成される results XML にのみ埋め込まれる。性能値を確認するときは
`--batch` で実行し、XML を直接参照する。README への性能値の自動反映は行わない。

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
 