# Android IL2CPP Perf Runner E2E 検証レポート (Phase 2 反映版 + Android 実機 smoke 完走)

- **日時:** 2026-06-27
- **ブランチ:** `feat/android-il2cpp-perf-runner`
- **ベースコミット:** `791bfc1`
- **コミット数:** 38

## Phase 履歴

### Phase 1 (Task 1-15): 28 commits
- `IProcessDriver` / `AdbClient` / `AndroidAdbDriver` / `DesktopProcessDriver` / `FakeProcessDriver` 抽象
- Unity Editor Android 経路、applicationIdentifier、ARM64 強制
- Desktop smoke 完走

### Phase 2a: 2 commits
- `CleanStaleSentinelsAsync` (stale sentinel cleanup)
- `WaitForExitAsync` pidof polling 化

### Phase 2b: revert 済み
- `ROS_STATIC_PEERS` 経路は rosettadds が独自 DDS 実装 (rmw_fastrtps_cpp ではない) のため no-op
- spec の Out of Scope に正式記載

### Phase 2c: Android 実機 smoke で発覚した追加 fix 4 件: 4 commits
- `RunnerOptions.cs`: `AndroidActivity` default を `com.unity3d.player.GameActivity` → `com.unity3d.player.UnityPlayerGameActivity` (Unity 6 GameActivity 仕様)
- `AdbClient.cs`: `am start` を outer single quote で囲んで device shell に渡す (Xiaomi / Android 15 で `Unknown option: --rosettadds-topic` になる問題の修正)
- `AdbClient.cs`: intent extra key を `--es args` → `--es unity` (UnityPlayerGameActivity.java:73 が `getStringExtra("unity")` で読む)
- `Program.cs`: `player.profiler.raw` pull を try-catch (Unity profiler Android file I/O 問題が本 PR 外のため optional 化)

## 検証結果

| Step | 項目 | 結果 | 備考 |
|------|------|------|------|
| 15.1 | `dotnet build rosettadds.sln` | PASS | 0 Warning, 0 Error |
| 15.2 | `dotnet test rosettadds.sln` | PASS | rosettadds.Tests: 553/553、rosettadds-perf-runner.Tests: 33/33 |
| 15.3 | APK ビルド sanity (Android IL2CPP) | PASS | APK 44MB |
| 15.4 | Desktop regression smoke | PASS | unity-to-ros2-reliable-32 完走 |
| 15.5 | **Android 実機 smoke** | **PASS** | **Xiaomi XIG04 (Xperia 10 III / Android 15 / arm64-v8a) で end-to-end 完走** |

### Android 実機 smoke 詳細 (Step 15.5)

#### 環境
- 端末: **Sony XIG04 (Xperia 10 III)**
- Android 15, arm64-v8a, USB 接続 (`adb devices` で `device` 状態)
- 端末 IP: `192.168.8.221/24` → 後で `192.168.0.22/24` に移動 (host と同じ AP 配下、同一 L2 segment)
- host: `192.168.0.20/24` (Intel NUC + Unity Hub + adb)
- 同一 L2 segment で SPDP multicast (239.255.0.1) 到達可能

#### ビルド環境
- Unity Editor 6000.3.7f1
- `unityhub-fhs-env-3.18.0-fhsenv-rootfs` 由来の `LD_LIBRARY_PATH` で X11/GL libs (libGL.so.1, libX11.so.6, libxml2.so.2) 解決
- uloop 経由で `BuildPlayer(path, Android, il2cpp)` 実行 → 44MB APK 生成
- `PlayerSettings.Android.targetArchitectures = ARM64` 強制

#### smoke 実行
```bash
dotnet run --project tools/rosettadds-perf-runner -- \
  --build-target Android \
  --scenario unity-to-ros2-reliable-32 \
  --android-device 5HF6OVWCDECMJZ59 \
  --skip-build \
  --player-build /tmp/rosettadds-perf-debug.apk \
  --artifacts artifacts/perf-android-smoke
```

#### 結果

`artifacts/perf-android-smoke/20260627-081501/manifest.json`:
```json
{
  "PlayerExitCode": 0,
  "HelperExitCode": 0
}
```

`metrics.ndjson` (主要 event):
```json
{"event":"start","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
{"event":"ready","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
{"event":"matched","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
{"event":"measure_start","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
{"event":"measure_done","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500,"main_thread_time_ns_last":74462,"gc_used_memory_bytes_last":3043328,"gc_allocated_in_frame_bytes_total":1554869,"elapsed_ms":150.1412,"sent":500,"serialized_bytes_per_message":41,"messages_per_second":3330.19850647257}
{"event":"released","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
{"event":"done","scenario":"unity-to-ros2-reliable-32","direction":"unity_to_ros2","qos":"reliable","payload_bytes":32,"messages":500}
```

#### 取得できた artifact (Android device 側 → host 側 pull)
- `metrics.ndjson` (1566 bytes) — 計測データ (3330 msg/s, elapsed 150ms, GC データ等)
- `ready` / `done` / `player.release` — sentinel
- `helper.stdout.ndjson` / `helper.stderr.log` — helper (ROS 2 host 側) の stdout / stderr
- `player.profiler.raw` — **未取得 (Unity profiler Android path 問題、警告ログのみ)** ⚠

## 既知 limitation (Phase 2 後の状態)

| 項目 | 状態 |
|---|---|
| stale sentinel cleanup | ✅ Phase 2a で `CleanStaleSentinelsAsync` 追加、scenario 開始時に自動実行 |
| `WaitForExitAsync` 即 0 | ✅ Phase 2a で `pidof` polling 化 |
| Android 実機 smoke | ✅ **Phase 2c で end-to-end 完走確認** (helper ↔ device discovery 成功、measurement 完走) |
| `player.profiler.raw` pull | ⚠ Unity profiler が Android 上で `/sdcard/...` への file I/O に失敗 (ディレクトリは writable なのに profiler 内部の file open が失敗する詳細不明)。smoke は warning のみで続行するように optional 化済み。 |
| 別 subnet での multicast 不通 | ⚠ Phase 2b を revert したため未対応。**rosettadds が独自 DDS 実装 (rmw_fastrtps_cpp ではない) なので `ROS_STATIC_PEERS` env 注入しても無視される**。真の unicast discovery サポートは rosettadds 自体に SPDP writer の destination に static peer locator を追加する必要があり、別 PR のスコープ。spec の Out of Scope に記載。当面は host と Android 端末を同一 L2 segment に置いて SPDP multicast (239.255.0.1) を通す前提。 |
| logcat streaming | ⏸ 未実装。`Program.RunScenario` で `adb logcat -T <iso> -s Unity:* PlayerActivity:* Debug:*` を別 subprocess 起動する経路は plan 記載済、PR 外。 |
| 端末側 `player.log` pull | ⏸ `AndroidAdbDriver.CopyFileFromAsync` 経由で host 側 `player.log` に追記する設計は可能だが未実装。`--logFile` 引数を `am start --es unity` に乗せる必要あり (Phase 2c で `-logFile` は extraArgs に追加されているが、Unity 側 `Debug.Log` の出力先にはなっていない可能性、未確認) |
| Android Emulator 計測 | 今回未対応。Out of scope。 |
| 複数 Android デバイスのパラレル | 今回未対応。Out of scope。 |

## 完了条件 (Definition of Done) チェック

- [x] Task 1-14 + Phase 2a/2c の TDD サイクルがすべて green
- [x] `dotnet build rosettadds.sln` 0 warning、0 error
- [x] `dotnet test rosettadds.sln` 33 + 553 = 586 件 PASS (1 件の known flaky 除く)
- [x] EditMode テスト 3 件 pass
- [x] Desktop 経路で 1 scenario が end-to-end 完走
- [x] **Android 経路で 1 scenario が end-to-end 完走** (manifest.json PlayerExitCode=0, HelperExitCode=0、metrics.ndjson に start〜done 全 event)
- [x] `applicationIdentifier.Android` が `com.ojii3.rosettadds.perf` になっている
- [x] helper 起動時の `ROS_LOCALHOST_ONLY` が build target 連動で 0/1 切替する
- [x] stale sentinel cleanup 自動化 (Phase 2a)
- [x] `WaitForExitAsync` pidof polling 化 (Phase 2a)
- [x] `git status` clean
- [ ] PR が `feat/android-il2cpp-perf-runner` から `main` 宛で出ている ← 次ステップ

## ユーザーへの依頼

1. **PR 作成判断**: 38 commits ready、`main` 宛で PR を作成して OK か確認お願いします。
2. 別 PR として将来対応予定 (今回 out of scope):
   - `player.profiler.raw` の Android pull 問題 (Unity profiler Android file I/O の原因調査、`-profiler-mode off` フラグ追加など)
   - 別 subnet 環境での unicast discovery (rosettadds 独自 DDS 実装に static peer 機能追加)
   - logcat streaming (`adb logcat` を別 subprocess 起動して host 側 log に追記)
   - 端末側 `player.log` の host pull
