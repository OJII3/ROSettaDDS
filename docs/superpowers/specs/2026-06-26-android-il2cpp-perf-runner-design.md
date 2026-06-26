# Android IL2CPP Performance Runner Design

## Goal

`tools/rosettadds-perf-runner` を拡張し、Android 実機 (IL2CPP ビルド) 上で
`tools/rosettadds-perf` ハーネスを動かし、LAN 上の ROS 2 helper
(`tools/ros2-perf-helper`) と pub/sub させ、メトリクス (throughput, GC,
ProfilerRecorder) を host 側 artifact として回収する。既存 desktop 経路
(StandaloneLinux64 / StandaloneOSX) は無改修で並走させる。

## Constraints

- 対象 Player は Android ARM64 + IL2CPP ビルドのみ。Mono / x86 系は今回対象外。
- Android 端末と helper host は **同一 WiFi 上でマルチキャスト (239.255.0.1) が
  通る** 前提を取る。IGMP snooping で multicast が落ちる環境や USB テザリング
  のみでの運用は今回対象外 (将来、`ROS_STATIC_PEERS` ベースで再設計)。
- helper は host (Linux) 側で動かし、`ROS_LOCALHOST_ONLY=0` で外部到達可能にする。
  Android 側の player は `LocalhostOnly=false` で動く。
- ADB 接続は USB 1 台固定 (serial 省略時 auto-detect、複数端末時は明示エラー)。
- 既存 NDJSON プロトコル (`ready` / `matched` / `measure_start` /
  `measure_done` / `error`) と sentinel file 規約は据え置き。Android 上では
  `Application.persistentDataPath` 配下に置き、`adb pull` で host 側へ持ち出す。
- Unity Editor に Android Build Support + NDK がインストール済みであることを
  前提とする。未インストール時は Editor 側で明確なエラーが返る (runner 側では
  個別に detection しない)。

## Architecture

計測は 3 層のまま据え置き、Player と supervisor (runner) の間に ADB 制御層を
1 枚挟む。

1. **ROSettaDDS perf Player harness** (Ros2Unity/Assets/Perf)
   - Android Player 上で ROSettaDDS publisher / subscriber を作る。
   - 起動引数で scenario と role を受け取る (`Environment.GetCommandLineArgs()`)。
   - 計測開始 / 終了、delivery、throughput、ProfilerRecorder counter を
     `Application.persistentDataPath` 配下の JSON Lines に出力する。
   - Profiler の詳細 trace は Unity Player 起動引数 `-profiler-enable` と
     `-profiler-log-file` で `.raw` に保存する (Android 上の path も
     persistentDataPath 配下)。

2. **ROS 2 perf helper** (tools/ros2-perf-helper)
   - 既存実装のまま host (Linux) 側で動かす。
   - scenario orchestration は持たない。
   - Android から discovery されるために `ROS_LOCALHOST_ONLY=0` を runner が
     注入する。

3. **External supervisor** (tools/rosettadds-perf-runner)
   - .NET console tool。
   - Unity Editor で専用 Development Player を build する。
   - Build target = Android の場合、内部で **`IProcessDriver`** 抽象を使い
     `AndroidAdbDriver` を起点に Player の install / start / 同期 / log 取得 /
     metrics pull を行う。
   - Build target = StandaloneLinux64 / StandaloneOSX の場合、既存の
     `ProcessCapture` を `DesktopProcessDriver` でラップして使う。
   - `artifacts/perf/<run-id>/` に manifest、metrics、logs を保存する。

ROS 2 process 群は host 側で動かし、Android とは UDP multicast で話す。Unity
process から ROS 2 process を起動しない方針は維持。

## Driver Abstraction

`tools/rosettadds-perf-runner/IProcessDriver.cs` を新設し、Player の起動 /
同期 / 回収を担う操作を interface 化する。実装は 2 つ。

### `IProcessDriver` (interface)

```csharp
public interface IProcessDriver : IDisposable
{
    Task StartAsync(LaunchSpec spec, CancellationToken ct);
    Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct);
    Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
    void Kill();
    Stream OpenLogAsync(LogKind kind, CancellationToken ct);
    Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct);
}
```

`LaunchSpec` は起動に必要な情報を全部 bundle した record
(`ScenarioName`, `Direction`, `DomainId`, `Topic`, `Qos`, `PayloadBytes`,
`Messages`, `LocalhostOnly`, `RemotePersistentDir`, `LocalArtifactDir`,
`LogFiles`, `SentinelNames`, `PlayerArgs`, …)。
`LogKind` は `Stdout` / `Stderr`。

### `DesktopProcessDriver` (新実装、既存ロジックの薄いラッパ)

- 既存の `ProcessCapture` を内部に持ち、`StartAsync` / `WaitForExitAsync` /
  `OpenLogAsync` はほぼそのまま委譲。
- sentinel polling は `WaitForFile` を移植。
- 既存の `RunScenario` 内の `StartPlayer` / `StartHelper` / `WaitForFile` を
  段階的に `DesktopProcessDriver` 経由に refactor する。最初の PR では
  helper 側のみ driver 経由に切替え、Android 経路の `AndroidAdbDriver` と
  並走で動作確認する。Player 側の desktop driver 化は別 PR (本 spec の
  Out-of-scope ではないが、squash すると差分が大きくなるため分離)。

### `AndroidAdbDriver` (新実装)

- コンストラクタ: `string adbPath, string deviceSerial, string packageId,
  string activityComponent, string devicePersistentDir, string localArtifactDir`。
- `StartAsync`:
  1. `adb -s <serial> install -r <apk>` (失敗時は例外)。
  2. `adb -s <serial> shell am force-stop <packageId>`。
  3. `adb -s <serial> shell am start -W -n <packageId>/<activityComponent>
     --es args "<space-joined PlayerArgs>"`。
- `WaitForSentinelAsync`:
  - 100ms 間隔で `adb -s <serial> pull <devicePersistentDir>/<name>
    <localArtifactDir>/<name>` を試行。
  - exit code 0 → 取得成功 → `true` を返して完了。
  - exit code 1 → ファイル無し、retry。
  - exit code 1 以外 (transport 断など) → `IOException` を投げる。
  - タイムアウトで `false`。
- `WaitForExitAsync`:
  - `adb -s <serial> shell pidof <packageId>` を 200ms 間隔で確認。
  - 空文字 (プロセス無し) なら exit code 0 として返す。
  - タイムアウト時は process を `Kill()` して `TimeoutException`。
- `Kill`:
  - `adb -s <serial> shell am force-stop <packageId>`。
- `OpenLogAsync`:
  - 内部で `adb -s <serial> logcat -T "<start-iso>" -s Unity:* PlayerActivity:*
    Debug:*` を subprocess 起動し、`StreamReader` を `Stream` として返す。
  - 戻り値の `Stream` を `Dispose` すると内部 subprocess にも SIGTERM が
    飛ぶように `Dispose` 連動で実装する。runner 側は `Stream.CopyToAsync`
    で `player.stdout.log` / `player.stderr.log` に書き出してから Dispose
    する。
- `CopyFileFromAsync`:
  - `adb -s <serial> pull <devicePersistentDir>/<remoteName> <localPath>`。
  - 上書き可 (一時ファイルを 1 度 staging path に置いて mv する方式はとらない)。

## Scenario Model

既存シナリオ (`unity_to_ros2` / `ros2_to_unity` × `reliable` / `best_effort` ×
payload 32 / 1024 / 8192) をそのまま流用。Android 経路で追加で増える scenario は
無い。`PerfScenario.cs` への変更は無し。

## Player Startup Protocol

### CLI フラグ (player 側)

`PerfPlayerArguments.cs` に新フラグ `--rosettadds-localhost-only` (bool) を追加。
未指定時の既定は `true` (desktop 既存挙動と同一)。

`PerfPlayerEntry.CreateParticipant` の `LocalhostOnly = true` ハードコード
(`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:241`) を `args.LocalhostOnly` に置換。

### Android 上の path 規約

- `Application.persistentDataPath` (= `/sdcard/Android/data/<packageId>/files/`)
  配下に固定で subdirectory `rosettadds-perf` を作って運用する。
- 置くもの:
  - `ready` (single byte sentinel)
  - `done` (single byte sentinel)
  - `release` (ros2_to_unity scenario のみで必要なら)
  - `metrics.ndjson` (append-only)
  - `profiler.raw` (Unity Profiler capture; `-profiler-log-file` に渡す)
- 各 file path は CLI args (`--rosettadds-metrics-file` 等) で渡される。runner
  側で組み立てて `am start --es args` に連結する。

### 同期フロー

1. runner: `am start -W --es args "<...>"`。
2. player: 起動直後に participant start して `ready` sentinel を書く。
3. runner: `WaitForSentinelAsync("ready", 20s)` で同期。
4. helper: discovery 完了を待ち、`armed` を stdout に書く (ros2_to_unity のみ)。
5. runner: `helper --measure-start` の arming を確認後 `go` を stdin に送信。
6. 計測。
7. player: `done` sentinel 書く。
8. runner: `CopyFileFromAsync` で `metrics.ndjson` / `profiler.raw` を
   `artifacts/perf/<run-id>/<scenario>/` 配下へ pull。
9. `adb shell am force-stop` で player を落とす。

## Helper 環境変数

`Program.cs:201-206` の helper 起動処理で、現状は `ROS_LOCALHOST_ONLY=1` を
無条件設定している。これを build target 連動にする:

- `options.BuildTarget == "Android"`: `ROS_LOCALHOST_ONLY=0`。
- それ以外: `ROS_LOCALHOST_ONLY=1` (既存挙動)。

`RMW_IMPLEMENTATION=rmw_fastrtps_cpp` / `ROS_DOMAIN_ID=<n>` は据え置き。

## Ros2Unity 側変更

### `ProjectSettings/ProjectSettings.asset`

- `applicationIdentifier.Android: com.ojii3.rosettadds.perf` に変更。
- `AndroidMinSdkVersion: 25` (据え置き)。
- `AndroidTargetArchitectures: 2` (ARM64 only、据え置き)。
- `AndroidTargetSdkVersion: 0` (auto、据え置き)。

### `ROSettaDDSPerfPlayerBuilder.cs` (Editor)

- `ParseBuildTarget` で `Android` → `BuildTarget.Android` を受理。
- `BuildPlayer` の冒頭で target に応じて:
  - `NamedBuildTarget.Standalone` または `NamedBuildTarget.Android` を選び、
    `PlayerSettings.SetScriptingBackend(...)` と
    `PlayerSettings.SetApplicationIdentifier(...)` を `try/finally` で
    original 値に復元する。
- 既存 `BuildOptions.Development` は据え置き (Android でも Profiler が有効に
  なる)。

## Runner CLI (RunnerOptions)

| Flag | Default | 説明 |
| --- | --- | --- |
| `--build-target` | OS 依存 (Linux/Mac) | `StandaloneLinux64` / `StandaloneOSX` / `Android` を受理。 |
| `--adb` | `adb` (PATH 解決) | adb バイナリ path。 |
| `--android-device` | auto (`adb devices -l` の単独エントリ) | serial 文字列。複数端末時は `ArgumentException`。 |
| `--android-package` | `com.ojii3.rosettadds.perf` | applicationIdentifier。 |
| `--android-activity` | `com.unity3d.player.GameActivity` | Activity component。 |
| `--capture-frames` | 1200 | 据え置き。 |
| `--profiler-memory` | 256 MiB | 据え置き。 |
| `--profiler-mode` | `lean` | 据え置き。 |

`--build-target Android` 指定時、runner は `IProcessDriver` の
`AndroidAdbDriver` を生成して `RunScenario` に渡す。それ以外は
`DesktopProcessDriver`。

## Testing Strategy

### `tools/rosettadds-perf-runner.Tests` (新 xUnit プロジェクト)

`rosettadds.sln` に追加。`Microsoft.NET.Test.Sdk` + `xunit` + `FluentAssertions`
を package 参照。既存 `tests/rosettadds.Tests` とは別 assembly。

- `RunnerOptionsTests`
  - `--build-target Android` 受理。
  - `--build-target Foo` 拒否。
  - `--adb /custom/adb` が保持される。
  - `--android-device X` が保持される。
  - `--android-package Y` / `--android-activity Z` が保持される。
  - 既定値: `BuildTarget` OS 依存、`Adb=adb`、`AndroidDevice=null`、
    `Package=com.ojii3.rosettadds.perf`、
    `Activity=com.unity3d.player.GameActivity`。
- `AndroidAdbDriverTests`
  - `FakeAdb` (subprocess を spawn せず記録) を使った driver テスト。
  - `WaitForSentinelAsync`: 1 回目 pull 失敗 → 2 回目成功で `true`。
  - `WaitForSentinelAsync`: 全 pull 失敗で `false`。
  - `WaitForSentinelAsync`: pull 以外の exit code で `IOException`。
  - `WaitForExitAsync`: プロセス消滅で即座に 0 返却。
  - `StartAsync`: `install` / `force-stop` / `am start --es args "<...>"`
    の 3 コマンドが順に呼ばれる。
- `ProgramTests`
  - 既存 `RunScenario` のロジックを `IProcessDriver` 経由に refactor し、
    `FakeAdbDriver` / `FakeDesktopDriver` で scenario 完走 / 部分 fail を
    assert。
  - helper の `ROS_LOCALHOST_ONLY` が build target によって 0/1 に切替わる
    ことを assert。

### EditMode (`Ros2Unity/Assets/Tests/EditMode/PerfPlayerArgumentsTests.cs`)

- `--rosettadds-localhost-only true` parse。
- `--rosettadds-localhost-only false` parse。
- 未指定時に `true` (default)。
- 既存他フラグは無改修で parse できる (regression test)。

## Out Of Scope

- 複数 Android デバイスのパラレル / マルチ fanout 計測。
- adb over WiFi (USB 接続前提)。
- スクショ / 動画 / Android Studio Profiler live attach。
- Android Emulator 計測。
- Android 上のメモリリーク guard (IL2CPP の GC 挙動は Mono と違うので別途
  検討)。
- ユニキャスト discovery 経路 (`ROS_STATIC_PEERS` 等)。マルチキャストが
  通らないネットワーク形態は別 PR。
- Gradle 経由の player ビルド (Unity 6 既定の internal build で進める)。

## Risks

- `Environment.GetCommandLineArgs()` が Android 上で `am start --es args` を
  拾えるかは Unity 6 の GameActivity 仕様に依存。`adb logcat` で player log
  を出して arg 値を assert する smoke test を PR に含めて確認する。
- ARM64 / IL2CPP のみの最初の build で、native 依存 (libc / librt 等) が
  端末に無くて起動しない可能性。初回実機 smoke で見つけたらその都度対処。
- multicast が AP の IGMP snooping で落ちる WiFi 環境だと discovery でき
  ない。`scripts/ros2/verify_helper.sh` の延長で Android からの discovery
  smoke test を作って早期に気付けるようにしたい。
- 既存 desktop シナリオへの副作用: helper の `ROS_LOCALHOST_ONLY` 切替は
  既存 desktop 経路には入れず、build target が Android のときのみ `0` に
  する。desktop 経路では引き続き `1` のまま。実装は `Program.StartHelper`
  の `env` 注入ロジックに `if (options.BuildTarget == "Android") "0" else "1"`
  の分岐を入れる。
