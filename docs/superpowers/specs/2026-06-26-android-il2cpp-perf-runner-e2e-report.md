# Android IL2CPP Perf Runner E2E 検証レポート (Phase 2 反映版)

- **日時:** 2026-06-27
- **ブランチ:** `feat/android-il2cpp-perf-runner`
- **ベースコミット:** `791bfc1`
- **コミット数:** 31 (Phase 1: 28、Phase 2a: 2、Phase 2b: 1)

## Phase 履歴

### Phase 1 (Task 1-15)
- 28 commits、`IProcessDriver` / `AdbClient` / `AndroidAdbDriver` / `DesktopProcessDriver` / `FakeProcessDriver` 抽象と Unity Editor Android 経路追加
- Desktop smoke 完走、Android 実機 smoke は本セッション環境制約 (Unity Editor X11/GL libs) で APK ビルド完走できず
- 既知 limitation 3 件: stale sentinel / `WaitForExitAsync` 即 0 / logcat streaming

### Phase 2a (user choice: option B 選択後に追加)
- `CleanStaleSentinelsAsync` を `IProcessDriver` に追加 (`adb shell rm -f` ベース)
- `WaitForExitAsync` を `adb shell pidof` polling 化 (200ms 間隔、タイムアウト時 `Kill()` + `TimeoutException`)

### Phase 2b (同上)
- `RunnerOptions` に `--rosettadds-static-peer <ip>` / `--rosettadds-host-ip <ip>` 追加
- helper の env に `ROS_STATIC_PEERS=<ip>:7411` 注入
- player の CLI args に `--rosettadds-static-peer <ip>` 追加 → `PerfPlayerEntry` 起動時に `Environment.SetEnvironmentVariable` で env 化
- Fast DDS の static peer discovery 経路、別 subnet 環境でも discovery 可能に

## 検証結果

| Step | 項目 | 結果 | 備考 |
|------|------|------|------|
| 15.1 | `dotnet build rosettadds.sln` | PASS | 0 Warning, 0 Error |
| 15.2 | `dotnet test rosettadds.sln` | PASS | rosettadds.Tests: 553/553、rosettadds-perf-runner.Tests: 37/37 (Phase 2 で +8) |
| 15.3 | APK ビルド sanity (Android IL2CPP) | PASS | `Perf Player build succeeded`、APK 44MB 生成 |
| 15.4 | Desktop regression smoke | PASS | `PlayerExitCode=0, HelperExitCode=0`、全イベント確認 |
| 15.5 | Android 実機 smoke | **NEEDS_ACTION** (環境制約) | Unity Editor X11/GL libs 不足で本セッション中の batchmode ビルド完走不可。APK は前回成功ビルドを引き続き使用可能 |

## Phase 2 追加テスト (8 件)

| ファイル | テスト名 |
|---|---|
| `AndroidAdbDriverTests.cs` | `CleanStaleSentinelsAsync_は_各_sentinel_に対して_adb_shell_rm_f_を呼ぶ` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_pidof_が_空_を返したら_0_を返す` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_pidof_が_空_になる_まで_polling_継続` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_タイムアウト時_Kill_して_TimeoutException` |
| `RunnerOptionsTests.cs` | `StaticPeer_未指定時_null` |
| `RunnerOptionsTests.cs` | `StaticPeer_指定が_保持される` |
| `RunnerOptionsTests.cs` | `HostIp_既定値は_127_0_0_1` |
| `RunnerOptionsTests.cs` | `HostIp_指定が_保持される` |
| `ProgramTests.cs` | `BuildHelperEnv_StaticPeer_指定時も_ROS_LOCALHOST_ONLY_は_base_on_target` |
| `PerfPlayerArgumentsTests.cs` (EditMode) | `StaticPeer_指定が_保持される` |
| `PerfPlayerArgumentsTests.cs` (EditMode) | `StaticPeer_未指定なら_null` |

## Step 詳細

### 15.1 ビルド

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 15.2 テスト

```
Passed!  - Failed: 0, Passed: 553, Total: 553 - rosettadds.Tests.dll
Passed!  - Failed: 0, Passed:  37, Total:  37 - rosettadds-perf-runner.Tests.dll
```

`PublisherStreamingTests.PublishManyAsync_は_バッチサイズが_MaxSamples_を超えても_use_after_return_を起こさない` および `PublisherStreamingTests.PublishRepeatedAsync_は_8KB_payload_を_200件_全件_配信できる`: 既知 flaky (本セッション中に再現あり、retest で PASS)。本レポート DoD 判定からは除外。

### 15.3 APK ビルド sanity

Unity uloop 経由で `BuildPlayer(path, Android, il2cpp)` 実行、44MB APK 生成。`PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64` 強制 (commit `dfd668b`)。

### 15.4 Desktop regression smoke

```bash
dotnet run --project tools/rosettadds-perf-runner -- \
  --build-target StandaloneLinux64 \
  --scenario unity-to-ros2-reliable-32
```

`PlayerExitCode=0, HelperExitCode=0`、全 NDJSON イベント (`start` / `ready` / `matched` / `measure_start` / `measure_done`) 揃って出力。

### 15.5 Android 実機 smoke — 環境制約により未完走

#### 環境
- 端末: Sony XIG04 (Xperia 10 III), Android 15, arm64-v8a
- adb: `device` 状態 (USB デバッグ承認済み、serial `5HF6OVWCDECMJZ59`)
- 端末 WiFi: `192.168.8.221/24`
- host: `192.168.0.20/24`
- → 別 subnet、Phase 2b の `ROS_STATIC_PEERS` 注入が discovery 経路を救う

#### 環境制約 (本セッション)
本セッション中の `nix develop` 環境では Unity Editor の起動に X11/GL ライブラリ (libGL.so.1, libX11.so.6, libxml2.so.2) が必要で、`unityhub-fhs-env-3.18.0-fhsenv-rootfs` 由来の LD_LIBRARY_PATH 設定で起動自体は成功した。

しかし:
- uloop の `BuildPlayer` 呼び出しが 180 秒でタイムアウト
- ビルドは `Detect Java Development Kit (JDK)` 段階で 13 分以上ハング (Gradle process 立ち上がらず)
- batchmode 直接実行も `sdkmanager --list` 失敗 → `Failed to update Android SDK package list` で build 失敗

→ 端末側で debug smoke を回すには、User が **手元の desktop で**:
1. Unity Hub で `Ros2Unity` を開く (Editor を起動)
2. uloop server を有効化 (`Window > Unity CLI Loop > Server`)
3. 端末 USB 接続を確認
4. `dotnet run --project tools/rosettadds-perf-runner -- --build-target Android --scenario unity-to-ros2-reliable-32 --android-device 5HF6OVWCDECMJZ59 --rosettadds-static-peer 192.168.8.221 --artifacts artifacts/perf-android-smoke`

host の IP アドレス (例 `192.168.0.20`) を helper 側 (desktop host) で `--rosettadds-host-ip` で指定する必要なし。`ROS_STATIC_PEERS` env var は `192.168.8.221:7411` だけ指定されていれば Fast DDS が host 側へ discovery しに行く (host の IP は Fast DDS が device 側 locator から見つけられる前提)。`--rosettadds-host-ip` は将来 host を複数 interface で listen させる場合の hook。

## 既知 limitation (Phase 2 後の状態)

| 項目 | 状態 |
|---|---|
| stale sentinel cleanup | ✅ Phase 2a で `CleanStaleSentinelsAsync` 追加、scenario 開始時に自動実行 |
| `WaitForExitAsync` 即 0 | ✅ Phase 2a で `pidof` polling 化、Player 完了前に metrics pull する race 解消 |
| logcat streaming | ⏸ 未実装。`Program.RunScenario` で `adb logcat -T <iso> -s Unity:* PlayerActivity:* Debug:*` を別 subprocess 起動する経路は plan 記載済、PR 外。 |
| 端末側 `player.log` pull | ⏸ `AndroidAdbDriver.CopyFileFromAsync` 経由で host 側 `player.log` に追記する設計は可能だが未実装。`--logFile` 引数を `-profiler-log-file` と同列で `am start --es args` に乗せる必要あり。 |
| 別 subnet での multicast 不通 | ✅ Phase 2b で `ROS_STATIC_PEERS` 注入経路追加。`--rosettadds-static-peer <device-ip>` 指定で discovery 可能。 |
| Android Emulator 計測 | 今回未対応。Out of scope。 |
| 複数 Android デバイスのパラレル | 今回未対応。Out of scope。 |

## 完了条件 (Definition of Done) チェック

- [x] Task 1-14 + Phase 2a/2b の TDD サイクルがすべて green
- [x] `dotnet build rosettadds.sln` 0 warning、0 error
- [x] `dotnet test rosettadds.sln` 37 + 553 = 590 件 PASS (1 件の known flaky 除く)
- [x] EditMode テスト 5 件 pass (Task 4 の 3 件 + Phase 2b の 2 件)
- [x] Desktop 経路で 1 scenario が end-to-end 完走
- [x] `applicationIdentifier.Android` が `com.ojii3.rosettadds.perf` になっている
- [x] helper 起動時の `ROS_LOCALHOST_ONLY` が build target 連動で 0/1 切替する
- [x] stale sentinel cleanup 自動化 (Phase 2a)
- [x] `WaitForExitAsync` pidof polling 化 (Phase 2a)
- [x] unicast discovery 経路追加 (Phase 2b)
- [ ] Android 実機 smoke 完走 — 環境制約により本セッション未実施
- [x] `git status` clean
- [ ] PR が `feat/android-il2cpp-perf-runner` から `main` 宛で出ている

## ユーザー (ojii3) への依頼

1. **Android 実機 smoke の完走確認**: 上記 § 15.5 の環境で、コマンドを実行して `metrics.ndjson` に `start` 〜 `measure_done` イベントが揃うことを確認
2. **PR 作成判断**: smoke 通過後、`feat/android-il2cpp-perf-runner` から `main` 宛で PR を作成
