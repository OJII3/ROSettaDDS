# Android Perf Bottleneck Investigation Design

## Goal

USB 接続した Android 実機 (Sony XIG04 / Xperia 10 III / Android 15 / arm64-v8a) 上で
`tools/rosettadds-perf-runner` を `--scenario all` で完走させ、同一 HEAD (3c74790) の
Desktop (StandaloneLinux64) 計測値と fair に比較することで、Android 固有のボトルネック
(mobile CPU / ARM64 IL2CPP / Android file I/O / WiFi 経路) を特定する。

計測結果と分析は `docs/superpowers/specs/2026-06-27-android-bottleneck-investigation.md`
(findings) として記録し、Desktop baseline との per-scenario diff 表 + 残存ボトルネック
+ 推奨 next action を提示する。

## Scope

### In scope

- Unity Editor (6000.3.7f1) + uLoopMCP 経由で `3c74790` を Android (IL2CPP, ARM64) と
  Desktop (StandaloneLinux64) の双方で Player ビルド
- `tools/rosettadds-perf-runner --scenario all --capture-frames 1200` を両 target で
  完走 (9 scenario: `unity-to-ros2-reliable-{32,1024,1400,8000}` /
  `unity-to-ros2-best-effort-8192` / `ros2-to-unity-reliable-{32,1024}` /
  `ros2-to-unity-best-effort-{8192,32k}`)
- Android 計測値 vs Desktop 計測値の per-scenario diff (mps / us/msg / alloc/msg /
  GC samples / main_thread_ns / rtps_* メトリクス)
- 既存 findings (A〜D) と新規計測の突き合わせ、Android 環境で増幅しているボトルネックの
  切り分け
- 計測の run 結果と findings の 1 commit + 1 PR

### Out of scope (findings の next action に送る)

- `player.profiler.raw` pull 問題の修正 (Android 上の file I/O 失敗、Unity profiler
  内部の挙動要調査) — `metrics.ndjson` ベースで分析は可能なため次フェーズに送る
- logcat streaming 経路 (`adb logcat -s Unity:* PlayerActivity:* Debug:*`) の実装
- Android 端末側 `player.log` の host pull 経路
- 別 subnet 環境での unicast discovery (rosettadds 独自 DDS 実装に static peer locator
  追加が必要、別 PR スコープ)
- 残存ボトルネック A〜D のうち 1 件以上の修正実装 (計測と修正はフェーズ分割)

## Constraints

- **同一 L2 セグメント**: Android 端末 `192.168.0.22/24` と host `192.168.0.20/24` が
  同一 AP 配下にあり、SPDP multicast `239.255.0.1` が到達可能 (e2e レポートで検証済)。
  USB テザリング環境や IGMP snooping で multicast が落ちる環境は対象外。
- **build 環境**: Unity Editor に Android Build Support + NDK + ARM64 がインストール
  済み、uloop server が起動可能、Unity Hub (6000.3.7f1) の CLI から
  `unityhub-fhs-env-3.18.0-fhsenv-rootfs` 由来の `LD_LIBRARY_PATH` で X11/GL libs 解決。
- **device**: 1 台固定 (`5HF6OVWCDECMJZ59`)、複数端末や emulator は対象外。
- **build artifact**: 既存 `/tmp/rosettadds-perf-debug.apk` (`f66df12` 時点、
  2026-06-27 16:57 ビルド) は `97e446e` の lean ProfilerRecorders 未取り込みのため
  再ビルド必須。
- **build 時間**: 1 build あたり Unity Editor 起動 + ビルドで 5〜10 分を想定。
  2 build で 10〜20 分 + 計測時間。
- **計測時間**: 1 scenario あたり measure 窓が 1〜2 分、scenario 間 setUp + helper
  起動 + sentinel wait で 30〜60 秒。9 scenario でおよそ 20〜30 分。

## Architecture

計測フローは既存 `tools/rosettadds-perf-runner` の据え置きで、build 経路のみ
2 段 (Android + Desktop) に増やす。

```
┌─────────────────────┐  uloop build   ┌──────────────────────────────┐
│ Unity Editor (CLI)  │ ─────────────► │ Android APK  (IL2CPP, ARM64) │
│  6000.3.7f1 + uloop │                │ /tmp/rosettadds-perf-android │
└─────────────────────┘                └──────────────┬───────────────┘
                                                     │ adb install
                                                     ▼
┌─────────────────────┐  uloop build   ┌──────────────────────────────┐
│ Unity Editor (CLI)  │ ─────────────► │ Desktop Player (Linux64)    │
│  (same instance)    │                │ artifacts/perf/build/...    │
└─────────────────────┘                └──────────────┬───────────────┘
                                                     │ direct exec
                                                     ▼
                              ┌──────────────────────────────────────┐
                              │ perf-runner (IProcessDriver)         │
                              │  Android: AndroidAdbDriver           │
                              │  Desktop: DesktopProcessDriver       │
                              └──────────┬───────────────────┬───────┘
                                         ▼                   ▼
                  artifacts/perf-android-all/<runId>/   artifacts/perf-desktop-baseline/<runId>/
                  ├── manifest.json                    ├── manifest.json
                  ├── <scenario>/metrics.ndjson        ├── <scenario>/metrics.ndjson
                  ├── <scenario>/helper.stdout.ndjson  ├── <scenario>/helper.stdout.ndjson
                  ├── <scenario>/helper.stderr.log     ├── <scenario>/helper.stderr.log
                  └── <scenario>/player.log            └── <scenario>/player.log
```

## Data Flow

1. **build (Android)**: uloop 経由で `ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("/tmp/rosettadds-perf-android.apk", "Android", "il2cpp")` 実行。
   `applicationIdentifier.Android = com.ojii3.rosettadds.perf`、`PlayerSettings.Android.targetArchitectures = ARM64`。
2. **build (Desktop)**: uloop 経由で `ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/build-android-investigation", "StandaloneLinux64", "il2cpp")` 実行。
3. **run (Android)**: `dotnet run --project tools/rosettadds-perf-runner -- --build-target Android --scenario all --android-device 5HF6OVWCDECMJZ59 --skip-build --player-build /tmp/rosettadds-perf-android.apk --artifacts artifacts/perf-android-all`。
4. **run (Desktop)**: `dotnet run --project tools/rosettadds-perf-runner -- --build-target StandaloneLinux64 --scenario all --skip-build --player-build artifacts/perf/build-android-investigation/ROSettaDDSPerfPlayer --artifacts artifacts/perf-desktop-baseline`。
5. **collect**: 両 run の `manifest.json` を jq で読み、各 scenario の `metrics.ndjson` を per-scenario diff 解析。helper 側 elapsed_ms / received / sent も横串で比較。
6. **analyze**: per-scenario で Android vs Desktop の mps 比、us/msg 比、alloc/msg 比を算出。既存 findings A〜D (Unity→ROS 2 8 KB ストリーミング / ProfilerRecorders ノイズ / ConfigureAwait / 全体 regression) の影響度が Android で増幅しているかを切り分け。
7. **document**: `docs/superpowers/specs/2026-06-27-android-bottleneck-investigation.md` に計測日 / 環境 / per-scenario diff 表 / 残存ボトルネック (Android 固有) / 推奨 next action (3〜5 項目) をまとめる。

## Error Handling

- **build 失敗**: `EnsureUloopSuccess` が uloop stdout の `Success` フィールドを判定。
  `IsTransientUloopState` ("Unity server is starting" / "Domain Reload in progress" /
  "Please wait a moment and try again") を検出したら最大 5 回 3 秒 sleep 後に再試行。
  5 回失敗で `InvalidOperationException` を投げて run 中断。
- **scenario 失敗**: 1 scenario の `PlayerExitCode != 0 || HelperExitCode != 0` で
  `failed = true` フラグを立て、残 scenario は継続。`manifest.json` に exit code
  が記録され、後段の analyze で skipped 扱いにできる。
- **adb 切断 / device 消失**: `WaitForSentinelAsync` の timeout (`TimeSpan.FromSeconds(20)`)
  を超えると `TimeoutException`。`AndroidAdbDriver` 経由で adb コマンド失敗時は
  `IOException` を投げ、ランナーは即座に中断。`--android-device` serial の再指定が
  必要。
- **multicast 不通**: `matched` sentinel が timeout する。e2e レポートの Phase 2b
  (revert 済) で対応予定だった unicast discovery は本 PR 範囲外。out of scope として
  findings に再記載。

## Validation (完了条件)

- [ ] 2 build (Android + Desktop) とも `dotnet run tools/rosettadds-perf-runner` 上で
      `--skip-build` なし完走 (build 経路も smoke)
- [ ] 9 scenario × 2 target = 18 計測がすべて `PlayerExitCode == 0 && HelperExitCode == 0`
- [ ] 各 scenario の `metrics.ndjson` に start / ready / matched / measure_start /
      measure_done / waiting_for_release / released / done (または同等の) event が
      すべて含まれる
- [ ] `manifest.json` の各 scenario に `MetricsPath` / `HelperStdoutPath` /
      `HelperStderrPath` が host 側の実在 path を指している
- [ ] findings doc に per-scenario diff 表 (Android mps / Desktop mps / 比率) +
      既存 findings A〜D との対比 + 推奨 next action が含まれている
- [ ] 1 commit + 1 PR で findings doc のみが main にマージ可能
- [ ] `git status` clean、ブランチ `perf/android-bottleneck-investigation` 上で作業

## Steps (実行順)

1. branch `perf/android-bottleneck-investigation` を `main` (3c74790) から作成
2. Unity Hub で `Ros2Unity` プロジェクトを開き、Window > Unity CLI Loop > Start Server
   (uloop server 起動)
3. uloop で Android Player ビルド (`/tmp/rosettadds-perf-android.apk`)
4. uloop で Desktop Player ビルド (`artifacts/perf/build-android-investigation/`)
5. perf-runner で Android 全 9 scenario 計測
6. perf-runner で Desktop 全 9 scenario 計測
7. jq + Python (or awk) で per-scenario diff 表を生成
8. findings doc を `docs/superpowers/specs/2026-06-27-android-bottleneck-investigation.md`
   に書く (計測日 / 環境 / per-scenario diff / ボトルネック / next action)
9. commit (findings doc のみ) → PR 作成 → レビュー → main マージ

## Risks

- **Unity Editor 起動失敗 / uloop server 未起動**: build 0 からやり直し。回避策は
  Editor を先に手動起動して安定化させる。
- **WiFi / multicast 経路の不安定**: 計測中に multicast が途切れると `matched` 失敗
  で scenario 落ち。回避策は host と端末を同一 AP に固定 (現状成立済)。
- **Android 端末スリープ / 省電力**: 計測中に CPU スロットリングが起きると結果が
  歪む。回避策は USB 接続中に `adb shell svc power stayon true` か Developer options
  の「充電中は画面消灯しない」を ON にする。e2e レポートでは明示されていないので
  計測前に有効化。
- **build 時間が長引く**: uloop 経由の Editor 操作は対話遅延で 10〜20 分になる可能性。
  中断リスクを見越してこまめに stdout / stderr を確認する。
- **player.profiler.raw 取得失敗**: Unity profiler Android file I/O 問題で
  `-profiler-log-file` 出力が空になる既知 limitation。`metrics.ndjson` ベースで
  分析は成立するが、CPU サンプル詳細 (call stack) は取得できない。findings に
  limitation として明記。
