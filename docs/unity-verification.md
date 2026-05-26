# Unity 検証・計測計画

`Ros2Unity` は rclsharp を Unity Package Manager のローカルパッケージとして読み込み、Unity Editor 上でコンパイル、通信動作、基本的な性能指標を継続確認するための検証プロジェクトとして使う。

## 目的

- Unity 6000.3 系 Editor で `com.ojii3.rclsharp` がパッケージとして解決・コンパイルできることを確認する。
- `DomainParticipant`、`Publisher<T>`、`Subscription<T>` を Unity のテストランナーから起動し、`std_msgs/msg/String` 相当の publish/subscribe が成立することを確認する。
- 通信処理のバッチ時間、messages/sec、serialized bytes/sec、管理ヒープ差分、Unity Profiler のメモリ指標を EditMode テストで記録する。
- 反復 publish/subscribe と create/dispose の後に retained memory を確認し、明らかなリークを EditMode テストで失敗させる。
- 外部 ROS 2 環境や OS の multicast 設定に依存しない計測を先に固定し、外部相互通信は別の手動/CI ジョブで拡張できる形にする。

## 検証範囲

初期の自動検証は `LoopbackHub` を使う。これは同一プロセス内で RTPS transport の契約を満たすため、Unity Editor のバッチ実行でも安定して通信経路を測れる。

計測対象:

- Unity package import: `Ros2Unity/Packages/manifest.json` から `../../src/rclsharp` を参照する。
- Smoke: 2 つの `DomainParticipant` 間で `StringMessage` を複数件送受信し、順序と件数を確認する。
- Throughput: payload サイズ別に warmup 後の publish/subscribe batch を複数回実行し、受信完了までの経過時間、messages/sec、serialized bytes/sec、平均 ms/message を記録する。
- Leak guard: participant / publisher / subscription / transport の create/dispose を繰り返し、full GC 後の managed heap と Unity mono used memory の retained delta を記録し、閾値を超えたら失敗させる。

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

`Ros2Unity` を Unity Editor で開いている間は、Unity の制約で同じ project path を batchmode から開けない。その場合は Editor を閉じるか、検証用に複製した project path を `UNITY_PROJECT_PATH` で指定する。

同じ処理をスクリプト側で行う場合:

```sh
UNITY_USE_TEMP_PROJECT=1 scripts/unity/run_unity_editmode_tests.sh
```

出力先:

- `artifacts/unity/editmode-results.xml`
- `artifacts/unity/unity-editmode.log`

`artifacts/` は計測結果の生成物なのでコミットしない。

`scripts/unity/run_unity_editmode_tests.sh` が成功した場合は、`editmode-results.xml`
に埋め込まれた Unity Performance Testing の結果を読み取り、`README.md` 末尾の
`rclsharp-local-performance` 管理ブロックを最新のローカル計測結果に差し替える。
GitHub Actions には Unity 計測を組み込まず、性能値の更新はローカル実行時だけ行う。

既存の `editmode-results.xml` から README だけ更新し直す場合:

```sh
scripts/unity/update_readme_performance.py
```

## 判定方針

Smoke test は件数・順序・タイムアウトで失敗させる。

Throughput test は現時点では閾値で失敗させず、Unity Performance Testing の sample group に数値を記録する。閾値は複数回のローカル/CI 実測から baseline を作ってから導入する。

Leak guard は throughput と違い、反復後に full GC を挟んだ retained delta に閾値を置く。Unity Editor 自体の一時キャッシュや package 側の初回初期化を避けるため、最初の cycle は warmup として baseline から外す。

初期閾値は managed heap retained delta 8 MiB、Unity mono used retained delta 64 MiB とする。通信速度は 32 B、1024 B、8192 B の `StringMessage.Data` payload で測る。

記録する主な sample group:

- `rclsharp.throughput.<payload>B.elapsed_ms`
- `rclsharp.throughput.<payload>B.messages_per_second`
- `rclsharp.throughput.<payload>B.serialized_bytes_per_second`
- `rclsharp.throughput.<payload>B.mean_message_ms`
- `rclsharp.leak.managed_heap_retained_bytes`
- `rclsharp.leak.unity_mono_used_retained_bytes`
- `rclsharp.leak.unity_total_allocated_delta_bytes`

## Unity 公式 MCP

参考: [Get started with Unity MCP](https://docs.unity3d.com/Packages/com.unity.ai.assistant@2.9/manual/integration/unity-mcp-get-started.html)

Unity 公式 MCP は Unity Assistant パッケージ (`com.unity.ai.assistant`) から提供されるため、`Ros2Unity/Packages/manifest.json` にプロジェクト依存として追加している。グローバルな Codex 設定には追加せず、リポジトリ内の `.codex/config.toml` と `.mcp.json` に MCP server 設定だけを置く。

使う前に Unity Editor で `Ros2Unity` を開き、`Edit > Project Settings > AI > Unity MCP Server` の `Unity Bridge` が `Running` になっていることを確認する。Unity は起動時に relay binary を `~/.unity/relay/` へ配置し、MCP client はその binary を `--mcp` 付きで起動する。

このリポジトリの設定は `scripts/unity/unity_mcp_relay.sh` を起動し、スクリプト内で `--project-path <repo>/Ros2Unity` を付けて公式 relay を呼び出す。複数の Unity Editor が起動していても、このプロジェクトの Editor instance へ接続するため。

relay path を明示的に差し替える場合は `UNITY_MCP_RELAY`、接続先プロジェクトを差し替える場合は `UNITY_PROJECT_PATH` を環境変数で指定する。
