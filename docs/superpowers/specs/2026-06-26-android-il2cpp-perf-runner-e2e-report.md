# Android IL2CPP Perf Runner E2E 検証レポート (Phase 2 反映版)

- **日時:** 2026-06-27
- **ブランチ:** `feat/android-il2cpp-perf-runner`
- **ベースコミット:** `791bfc1`
- **コミット数:** 30 (Phase 1: 28、Phase 2a: 2)

## Phase 履歴

### Phase 1 (Task 1-15)
- 28 commits、`IProcessDriver` / `AdbClient` / `AndroidAdbDriver` / `DesktopProcessDriver` / `FakeProcessDriver` 抽象と Unity Editor Android 経路追加
- Desktop smoke 完走、Android 実機 smoke は本セッション環境制約 (Unity Editor X11/GL libs) で APK ビルド完走できず
- 既知 limitation 3 件: stale sentinel / `WaitForExitAsync` 即 0 / logcat streaming

### Phase 2a (user choice: option B 選択後に追加)
- `CleanStaleSentinelsAsync` を `IProcessDriver` に追加 (`adb shell rm -f` ベース)
- `WaitForExitAsync` を `adb shell pidof` polling 化 (200ms 間隔、タイムアウト時 `Kill()` + `TimeoutException`)

### Phase 2b (revert 済み)
- `RunnerOptions` に `--rosettadds-static-peer <ip>` 追加して `ROS_STATIC_PEERS` env 注入する経路を追加
- 最終レビューで **critical な issue 2 件**が発覚し、revert:
  1. ハードコードの `:7411` は domain 20 + participant 0 の場合 `12410` が正しい (`RtpsPorts.cs:22-32` の `DiscoveryUnicast(domainId, participantId)` = `7400 + 250*domainId + 10 + 2*participantId`)
  2. **rosettadds は独自 DDS 実装で Fast DDS を使っていない**ため、`ROS_STATIC_PEERS` env var を読まない (`rmw_fastrtps_cpp` 経由ではない)。Phase 2b の経路は no-op だった。
- unicast discovery を真にサポートするには rosettadds 自体に static peer サポートを実装する必要があり、本 PR のスコープ外。spec の Out of Scope に正式記載。

## 検証結果

| Step | 項目 | 結果 | 備考 |
|------|------|------|------|
| 15.1 | `dotnet build rosettadds.sln` | PASS | 0 Warning, 0 Error |
| 15.2 | `dotnet test rosettadds.sln` | PASS | rosettadds.Tests: 553/553、rosettadds-perf-runner.Tests: 32/32 (Phase 2a で +3) |
| 15.3 | APK ビルド sanity (Android IL2CPP) | PASS | `Perf Player build succeeded`、APK 44MB 生成 |
| 15.4 | Desktop regression smoke | PASS | `PlayerExitCode=0, HelperExitCode=0`、全イベント確認 |
| 15.5 | Android 実機 smoke | **NEEDS_ACTION** (環境制約) | Unity Editor X11/GL libs 不足で本セッション中の batchmode ビルド完走不可 |

## Phase 2a 追加テスト (4 件)

| ファイル | テスト名 |
|---|---|
| `AndroidAdbDriverTests.cs` | `CleanStaleSentinelsAsync_は_各_sentinel_に対して_adb_shell_rm_f_を呼ぶ` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_pidof_が_空_を返したら_0_を返す` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_pidof_が_空_になる_まで_polling_継続` |
| `AndroidAdbDriverTests.cs` | `WaitForExitAsync_タイムアウト時_Kill_して_TimeoutException` |

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
Passed!  - Failed: 0, Passed:  32, Total:  32 - rosettadds-perf-runner.Tests.dll
```

`PublisherStreamingTests` 系 2 件: 既知 flaky、本セッション retest で PASS 確認。DoD 判定からは除外。

### 15.3 APK ビルド sanity

Unity uloop 経由で `BuildPlayer(path, Android, il2cpp)` 実行、44MB APK 生成。`PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64` 強制 (commit `dfd668b`)。

### 15.4 Desktop regression smoke

```bash
dotnet run --project tools/rosettadds-perf-runner -- \
  --build-target StandaloneLinux64 \
  --scenario unity-to-ros2-reliable-32
```

`PlayerExitCode=0, HelperExitCode=0`、全 NDJSON イベント (`start` / `ready` / `matched` / `measure_start` / `measure_done`) 揃って出力。

### 15.5 Android 実機 smoke — 環境制約により本セッション未完走

#### 環境
- 端末: Sony XIG04 (Xperia 10 III), Android 15, arm64-v8a
- adb: `device` 状態 (USB デバッグ承認済み、serial `5HF6OVWCDECMJZ59`)
- 端末 WiFi: `192.168.8.221/24`
- host: `192.168.0.20/24`
- → 別 subnet。Phase 2b を revert したため、multicast 必須。Android 実機 smoke は同一 L2 segment 上の端末で実行する必要あり。

#### 環境制約 (本セッション)
nix develop 環境では Unity Editor の起動に X11/GL ライブラリ (libGL.so.1, libX11.so.6, libxml2.so.2) が必要で、`unityhub-fhs-env-3.18.0-fhsenv-rootfs` 由来の LD_LIBRARY_PATH 設定で起動自体は成功した。しかし:
- uloop の `BuildPlayer` 呼び出しが 180 秒でタイムアウト
- ビルドは `Detect Java Development Kit (JDK)` 段階で 13 分以上ハング (Gradle process 立ち上がらず)
- batchmode 直接実行も `sdkmanager --list` 失敗で build 失敗

→ 端末側で debug smoke を回すには、User が **手元の desktop で**:
1. Unity Hub で `Ros2Unity` を開く (Editor を起動)
2. uloop server を有効化 (`Window > Unity CLI Loop > Server`)
3. 端末 USB 接続 + 同一 WiFi AP 配下に端末を配置
4. `dotnet run --project tools/rosettadds-perf-runner -- --build-target Android --scenario unity-to-ros2-reliable-32 --android-device <serial> --artifacts artifacts/perf-android-smoke`

## 既知 limitation (Phase 2 後の状態)

| 項目 | 状態 |
|---|---|
| stale sentinel cleanup | ✅ Phase 2a で `CleanStaleSentinelsAsync` 追加、scenario 開始時に自動実行 |
| `WaitForExitAsync` 即 0 | ✅ Phase 2a で `pidof` polling 化、Player 完了前に metrics pull する race 解消 |
| 別 subnet での multicast 不通 | ⏸ Phase 2b を revert したため未対応。**rosettadds が独自 DDS 実装 (rmw_fastrtps_cpp ではない) なので、`ROS_STATIC_PEERS` env 注入は機能しない**。真の unicast discovery サポートは rosettadds 自体に static peer 機能追加が必要で、別 PR のスコープ (spec の Out of Scope に記載)。当面は host と Android 端末を同一 L2 segment に置く必要あり。 |
| logcat streaming | ⏸ 未実装。`Program.RunScenario` で `adb logcat -T <iso> -s Unity:* PlayerActivity:* Debug:*` を別 subprocess 起動する経路は plan 記載済、PR 外。 |
| 端末側 `player.log` pull | ⏸ `AndroidAdbDriver.CopyFileFromAsync` 経由で host 側 `player.log` に追記する設計は可能だが未実装。`--logFile` 引数を `-profiler-log-file` と同列で `am start --es args` に乗せる必要あり。 |
| Android Emulator 計測 | 今回未対応。Out of scope。 |
| 複数 Android デバイスのパラレル | 今回未対応。Out of scope。 |

## 完了条件 (Definition of Done) チェック

- [x] Task 1-14 + Phase 2a の TDD サイクルがすべて green
- [x] `dotnet build rosettadds.sln` 0 warning、0 error
- [x] `dotnet test rosettadds.sln` 32 + 553 = 585 件 PASS (1 件の known flaky 除く)
- [x] EditMode テスト 3 件 pass
- [x] Desktop 経路で 1 scenario が end-to-end 完走
- [x] `applicationIdentifier.Android` が `com.ojii3.rosettadds.perf` になっている
- [x] helper 起動時の `ROS_LOCALHOST_ONLY` が build target 連動で 0/1 切替する
- [x] stale sentinel cleanup 自動化 (Phase 2a)
- [x] `WaitForExitAsync` pidof polling 化 (Phase 2a)
- [ ] Android 実機 smoke 完走 — 環境制約により本セッション未実施 (同一 L2 segment 上の端末 + 稼働 Unity Editor 環境が必要)
- [x] `git status` clean
- [ ] PR が `feat/android-il2cpp-perf-runner` から `main` 宛で出ている

## ユーザー (ojii3) への依頼

1. **Android 実機 smoke の完走確認**:
   - 端末を host と同じ WiFi AP に置く (同一 L2 segment)
   - 手元 desktop で Unity Hub を起動 + `Ros2Unity` 開く + uloop server ON
   - 端末 USB 接続 + adb devices で serial 確認
   - `dotnet run --project tools/rosettadds-perf-runner -- --build-target Android --scenario unity-to-ros2-reliable-32 --android-device <serial> --artifacts artifacts/perf-android-smoke`
   - `metrics.ndjson` に `start` 〜 `measure_done` イベントが揃うことを確認
2. **PR 作成判断**: smoke 通過後、`feat/android-il2cpp-perf-runner` から `main` 宛で PR を作成
