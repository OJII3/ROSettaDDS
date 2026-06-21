# Unity Player Profiler Performance Measurement Design

## Goal

Unity Player 上の ROSettaDDS について、Unity Profiler を主軸にした再現性の高い性能計測環境を作る。
計測結果は Profiler capture、数値 metrics、実行 manifest、Player / helper log を同じ run id の artifact として保存し、
ボトルネック探索に使える状態にする。

## Constraints

- Unity Editor / Unity Player は ROS 2 CLI や ROS 2 の環境変数を要求しない。
- ROS 2 を必要とする処理は、ROS 2 devShell 内で動く外部 supervisor と helper process に閉じる。
- 主対象は Standalone Player。Editor / PlayMode の性能値は初期対象外。
- 初期対象は同一マシン loopback、ROS 2 Humble、Fast DDS (`rmw_fastrtps_cpp`)。
- 既存の Unity PlayMode perf test が ROS 2 helper を起動する構造は廃止する。

## Architecture

計測は 3 層に分ける。

1. **ROSettaDDS perf Player harness**
   - Unity Player 内で ROSettaDDS publisher / subscriber を作る。
   - 起動引数で scenario と role を受け取る。
   - 計測開始 / 終了、delivery、throughput、ProfilerRecorder counter を JSON Lines で出力する。
   - Profiler の詳細 trace は Unity Player 起動引数 `-profiler-enable` と `-profiler-log-file` で `.raw` に保存する。

2. **ROS 2 perf helper**
   - ROS 2 / rclcpp process として `pub` または `sub` endpoint だけを担当する。
   - stdout JSON Lines の `ready` / `armed` / `done` / `error` event で supervisor と同期する。
   - scenario 全体の orchestration は行わない。

3. **External supervisor**
   - ROS 2 devShell 内で起動する .NET console tool とする。
   - Unity Editor で専用 Development Player を build する。
   - ROS 2 helper と Unity Player を外部 process として起動し、stdout / stderr を収集する。
   - Player に `-batchmode -nographics -profiler-enable -profiler-log-file ...` を渡す。
   - `artifacts/perf/<run-id>/` に manifest、metrics、logs、Profiler capture を保存する。

Unity process から ROS 2 process を起動しない。ROS 2 環境を知っているのは supervisor と helper だけにする。

## Scenario Model

初期 scenario は既存の ROS 2 perf 計測から必要最小限に絞る。

- Direction:
  - `unity_to_ros2`: Player publisher、ROS 2 helper subscriber
  - `ros2_to_unity`: ROS 2 helper publisher、Player subscriber
- QoS:
  - `reliable`
  - `best_effort`
- Payload:
  - 32 B
  - 1024 B
  - 8192 B
- Fanout:
  - 初期は 1

Fanout 2 以上や 32768 B payload は後続で追加する。初期版は Profiler capture の再現性と artifact 体系を優先する。

## Player Harness Contract

Player は次の独自引数を受け取る。

- `--rosettadds-perf`
- `--scenario <name>`
- `--direction unity_to_ros2|ros2_to_unity`
- `--domain-id <int>`
- `--topic <absolute ROS topic>`
- `--qos reliable|best_effort`
- `--payload-bytes <int>`
- `--messages <int>`
- `--ready-file <path>`
- `--done-file <path>`
- `--metrics-file <path>`

Player は JSON Lines を metrics file と stdout の両方に出す。主な event は次の通り。

- `ready`: DDS endpoint 作成と participant start 完了
- `matched`: remote endpoint との match 完了
- `measure_start`: steady-state 計測開始
- `measure_done`: elapsed、sent / received、delivery rate、serialized bytes、ProfilerRecorder counter を含む
- `error`: 失敗理由

`ready-file` と `done-file` は supervisor からの同期を簡単にするための sentinel file とする。
stdout JSON Lines だけに依存すると、Player log buffering や Test Runner 経由の出力差分に影響されるため、同期は file sentinel を主に使う。

## Profiler Data

Supervisor は Player 起動時に次を渡す。

```text
-profiler-enable
-profiler-log-file <artifact-dir>/player.profiler.raw
-profiler-capture-frame-count <frames>
-profiler-maxusedmemory <bytes>
```

Player harness は ProfilerRecorder で、少なくとも次を metrics に記録する。

- Main Thread frame time
- GC / managed heap 系 memory counter
- system used memory
- total allocated memory が取得可能なら記録

ProfilerRecorder counter は Unity version / backend / platform で利用可否が変わるため、無効な counter は `available=false` として metrics に記録し、fallback 実装は持たない。

## Build And Execution

Supervisor の責務:

- Unity Editor path を検出または `UNITY_EDITOR` から読む。
- `Ros2Unity` project から専用 Player を build する。
- build backend は初期値 IL2CPP、切り分け用に Mono を選べる。
- ROS 2 helper が未 build なら明示的に失敗する。自動 `colcon build` は初期版では行わない。

想定入口:

```sh
nix develop
dotnet run --project tools/rosettadds-perf-runner -- \
  --backend il2cpp \
  --scenario reliable-1024 \
  --capture-frames 1200
```

薄い convenience script は追加してよいが、仕様と orchestration の本体は .NET tool に置く。

## Existing Code To Remove

次は新構成では不要になるため削除する。

- `Ros2Unity/Assets/Tests/Ros2Perf`
- `ROSettaDDS.UnityRos2Perf.Tests`
- Unity PlayMode test が ROS 2 helper process を起動するための C# wrapper / parser
- 旧 PlayMode perf test の実行手順

`tools/ros2-perf-helper` は endpoint process として残す。ただし scenario orchestration は持たせない。

## Testing

- .NET supervisor unit tests:
  - argument parsing
  - Unity executable / Player executable path resolution
  - JSON Lines event parsing
  - timeout / process exit classification
  - artifact manifest generation
- Unity EditMode tests:
  - Player harness argument parser
  - scenario definition validation
  - metrics writer
- ROS 2 helper smoke:
  - 既存 `scripts/ros2/verify_helper.sh` 相当で helper endpoint の正常性を確認
- End-to-end:
  - ROS 2 devShell 内で 1 scenario を実行し、Profiler `.raw`、metrics、manifest、logs が生成されることを確認

## Documentation

`docs/unity-verification.md` と `docs/interop.md` は新しい Player Profiler workflow に更新する。
旧 PlayMode perf 手順と、Unity から helper を起動する説明は削除する。
