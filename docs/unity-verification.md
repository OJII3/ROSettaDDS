# Unity 検証・計測計画

`Ros2Unity` は rclsharp を Unity Package Manager のローカルパッケージとして読み込み、Unity Editor 上でコンパイル、通信動作、基本的な性能指標を継続確認するための検証プロジェクトとして使う。

## 目的

- Unity 6000.3 系 Editor で `com.ojii3.rclsharp` がパッケージとして解決・コンパイルできることを確認する。
- `DomainParticipant`、`Publisher<T>`、`Subscription<T>` を Unity のテストランナーから起動し、`std_msgs/msg/String` 相当の publish/subscribe が成立することを確認する。
- 通信処理のバッチ時間、messages/sec、管理ヒープ差分、Unity Profiler のメモリ指標を EditMode テストで記録する。
- 外部 ROS 2 環境や OS の multicast 設定に依存しない計測を先に固定し、外部相互通信は別の手動/CI ジョブで拡張できる形にする。

## 検証範囲

初期の自動検証は `LoopbackHub` を使う。これは同一プロセス内で RTPS transport の契約を満たすため、Unity Editor のバッチ実行でも安定して通信経路を測れる。

計測対象:

- Unity package import: `Ros2Unity/Packages/manifest.json` から `../src/rclsharp` を参照する。
- Smoke: 2 つの `DomainParticipant` 間で `StringMessage` を複数件送受信し、順序と件数を確認する。
- Performance: warmup 後に複数メッセージを publish し、受信完了までの経過時間、messages/sec、managed heap delta、Unity allocated memory delta、Unity mono used memory delta を記録する。

対象外:

- 外部 `ros2` CLI / Fast DDS との相互通信。
- 実 NIC や multicast loopback の OS 設定に依存する計測。
- Player ビルド後の端末別プロファイル。

これらは自動検証が安定した後に、別ジョブとして追加する。

## 実行方法

Unity Editor が標準の Hub パスにある場合:

```sh
scripts/unity/run_unity_editmode_tests.sh
```

Editor パスを明示する場合:

```sh
UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.3.7f1/Unity.app/Contents/MacOS/Unity \
  scripts/unity/run_unity_editmode_tests.sh
```

出力先:

- `artifacts/unity/editmode-results.xml`
- `artifacts/unity/unity-editmode.log`

`artifacts/` は計測結果の生成物なのでコミットしない。

## 判定方針

Smoke test は件数・順序・タイムアウトで失敗させる。

Performance test は現時点では閾値で失敗させず、Unity Performance Testing の sample group に数値を記録する。閾値は複数回のローカル/CI 実測から baseline を作ってから導入する。
