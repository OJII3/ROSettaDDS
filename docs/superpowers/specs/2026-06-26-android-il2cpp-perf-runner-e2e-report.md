# Android IL2CPP Perf Runner E2E 検証レポート

- **日時:** 2026-06-27
- **ブランチ:** `feat/android-il2cpp-perf-runner`
- **ベースコミット:** `791bfc1`
- **Task 1-14 コミット数:** 23

## 検証結果

| Step | 項目 | 結果 | 備考 |
|------|------|------|------|
| 15.1 | `dotnet build rosettadds.sln` | PASS | 0 Warning, 0 Error |
| 15.2 | `dotnet test rosettadds.sln` | PASS | rosettadds.Tests: 553/553, rosettadds-perf-runner.Tests: 29/29 |
| 15.3 | APK ビルド sanity (Android IL2CPP) | PASS | `Perf Player build succeeded`, APK 44MB 生成 |
| 15.4 | Desktop regression smoke | PASS | `PlayerExitCode=0, HelperExitCode=0`, 全イベント確認 |
| 15.5 | Android 実機 smoke | NEEDS_ACTION | 端末は adb で認識 (`unauthorized`) だが USB デバッグ未承認 |

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
Passed!  - Failed: 0, Passed:  29, Total:  29 - rosettadds-perf-runner.Tests.dll
```

`PublisherStreamingTests.PublishRepeatedAsync_は_8KB_payload_を_200件_全件_配信できる`: flaky だが今回は PASS。

### 15.3 APK ビルド sanity

Unity batch mode (`-executeMethod ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.Build`) 経由。

**修正点:** `Target architecture not specified` エラーに対応。`BuildPlayer` メソッドに `PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64` を追加。

結果: 44MB の APK (`/tmp/rosettadds-perf-debug.apk`) 生成確認、`Exiting batchmode successfully now!`。

### 15.4 Desktop regression smoke

```bash
dotnet run --project tools/rosettadds-perf-runner -- \
  --build-target StandaloneLinux64 \
  --scenario unity-to-ros2-reliable-32 \
  --artifacts artifacts/perf-desktop-regression \
  --skip-build --player-build /tmp/rosettadds-perf-desktop
```

`manifest.json`:
```json
{
  "PlayerExitCode": 0,
  "HelperExitCode": 0
}
```

metrics.ndjson イベント: `start`, `ready`, `matched`, `measure_start`, `measure_done`, `waiting_for_release`, `released`, `done` — 全件確認。

### 15.5 Android 実機 smoke

- `adb devices` 出力: `5HF6OVWCDECMJZ59	unauthorized`
- `lsusb` で Xiaomi Redmi Note 3 を認識 (Bus 008 Device 006, ID 2717:ff08)
- USB デバッグ承認待ち状態。承認後再確認が必要。

### 15.6 コミットログ

`791bfc1..HEAD`: Task 1-14 の全 23 コミットが積まれている。

## ユーザー次のアクション

1. **USB デバッグ承認:** 端末の通知領域を確認し、USB 用途を「ファイル転送 (MTP)」に切替 → 「USB デバッグが許可されました」通知を確認 → 「この PC を信頼」ダイアログで「常に許可」 + 許可。
2. `adb devices` で `device` (authorized) を確認後、以下のコマンドで smoke 実行:
   ```bash
   dotnet run --project tools/rosettadds-perf-runner -- \
     --build-target Android \
     --scenario unity-to-ros2-reliable-32 \
     --android-device $(adb devices | grep -v List | awk 'NR==1{print $1}') \
     --artifacts artifacts/perf-android-smoke
   ```
3. 承認後の smoke 結果次第で PR 作成判断。

## 既知の Limitation

| Limitation | 詳細 |
|------------|------|
| stale sentinel | Task 13 review で指摘。`player.done` が古い run の残骸として残ると誤検出する可能性。今後の対応。 |
| `player.log` pull | Android 実機から `player.log` を自動 pull しない (adb pull 未実装)。手動または別途スクリプトで取得。 |
| `WaitForExitAsync` 即 0 | Android 実機で `am force-stop` 直後の `WaitForExitAsync` が即時 0 を返す問題 (Task 13 review)。軽微、実用上問題なし。 |
| `uloop` timeout | `uloop execute-dynamic-code` は 180s ハードコードタイムアウト。IL2CPP Android ビルドはこれを超過するため、Unity batch mode 直接実行が必要。 |
| `PlayerSettings.Android.targetArchitectures` | `BuildPlayer` に明示設定が必要。`Builder.cs` に修正済み。 |
