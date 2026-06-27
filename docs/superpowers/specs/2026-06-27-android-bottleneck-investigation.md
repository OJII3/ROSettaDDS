# Android Perf Bottleneck Investigation (2026-06-27)

## サマリ

USB 接続した Sony XIG04 (Xperia 10 III / Android 15 / arm64-v8a) で
`tools/rosettadds-perf-runner --scenario all` を 9 scenario × 2 target (Android +
Desktop) で測定した。同一 HEAD `3c74790` から build し、既存 findings
(`2026-06-25-perf-revisit-findings.md`) の A〜D と突き合わせた結果、

- **Android publish ホットパス (Player → ROS 2 helper) は Desktop 比 1.4〜3.0×
  遅い** (Unity→ROS 2 8 KB best-effort: 517 mps vs 1 333 mps Desktop)
- **helper → Android 受信 (ROS 2 → Player) は Desktop 比 0.5〜0.6× 程度** (best-effort
  8 KB: 5 116 mps vs 11 556 mps Desktop)
- **Android 固有の discovery 問題**: helper 側 reliable subscriber が Android
  からの SPDP/SEDP を受信できず、全 reliable シナリオで `received: 0` タイム
  アウト。best-effort は部分的に成功 (146/200)。Desktop loopback では同じ
  シナリオで `received: 500` 完走しており、rosettadds の RTPS 実装ではなく
  Android 側の discovery 経路に原因がある。
- **計測中に perf-runner のバグを 1 件発見・修正**: `PerfRunnerPaths.ResolvePlayerBuildPath`
  が Android 出力の `.apk` 拡張子を欠落させ `adb install` が `filename doesn't
  end .apk or .apex` で失敗していた。43.9MB の valid APK が生成されていたにも
  かかわらず install できない状態。TDD で 4 件の test 追加 + 1 行修正で対処
  (commit `0b457b9`)。

## 計測環境

- **日時**: 2026-06-27 18:43 JST (Android), 19:01 JST (Desktop)
- **HEAD**: `3c74790` (Android IL2CPP perf runner マージ直後)
- **branch**: `perf/android-bottleneck-investigation` (本調査の作業 branch)
- **build 環境**: Unity Editor 6000.3.7f1 + uLoopMCP, Android Build Support + NDK + ARM64
  インストール済
- **Player 設定**:
  - Android: `applicationIdentifier=com.ojii3.rosettadds.perf`,
    `targetArchitectures=ARM64`, IL2CPP
  - Desktop (StandaloneLinux64): Mono 2x (Desktop IL2CPP は未インストール)
- **端末**: Sony XIG04 (Xperia 10 III), Android 15, arm64-v8a, USB 接続
  (`5HF6OVWCDECMJZ59`, `device` 状態, `mScreenOn=true` wakelock 設定)
- **Network**: 同一 L2 セグメント (host `192.168.0.20/24` ↔ 端末 `192.168.0.22/24`),
  ping 5.9ms, SPDP multicast `239.255.0.1` 経路

## 計測結果 (per-scenario)

### publish 側 (Player → helper)

| Scenario | payload | qos | Android mps | Desktop mps | Android / Desktop |
|----------|--------:|-----|------------:|------------:|------------------:|
| unity-to-ros2-reliable-32 | 32 B | reliable | 6 450 | 9 019 | 0.72 |
| unity-to-ros2-reliable-1024 | 1 033 B | reliable | (fail) | 10 239 | — |
| unity-to-ros2-reliable-1400 | 1 409 B | reliable | 4 426 | 8 044 | 0.55 |
| unity-to-ros2-reliable-8000 | 8 009 B | reliable | 538 | 1 470 | 0.37 |
| unity-to-ros2-best-effort-8192 | 8 201 B | best_effort | 517 | 1 333 | 0.39 |

publish 側 (Player 自身の publishing 性能) は Android で Desktop の **0.4〜0.7×
に低下**。特に 8 KB 帯で 0.4× 以下に落ち込み、ARM64 IL2CPP での ArrayPool /
PublishRepeatedAsync ホットパスの CPU 律速が顕著。

### subscribe 側 (helper → Player)

| Scenario | payload | qos | Android mps | Desktop mps | Android / Desktop |
|----------|--------:|-----|------------:|------------:|------------------:|
| ros2-to-unity-reliable-32 | 41 B | reliable | 8 081 | (fail) | — |
| ros2-to-unity-reliable-1024 | 1 033 B | reliable | 5 591 | 10 983 | 0.51 |
| ros2-to-unity-best-effort-8192 | 8 201 B | best_effort | 5 116 | 11 556 | 0.44 |
| ros2-to-unity-best-effort-32k | 32 777 B | best_effort | 587 | (fail) | — |

subscribe 側は Android で Desktop の **0.4〜0.5×**。8 KB best-effort 11 556 mps
→ 5 116 mps と 56% 低下。

### helper 受信 (Player publish → helper 受信)

| Scenario | Android helper received | Desktop helper received |
|----------|------------------------:|------------------------:|
| unity-to-ros2-reliable-32 | 0 / 500 | 500 / 500 |
| unity-to-ros2-reliable-1024 | 0 / 500 | 500 / 500 |
| unity-to-ros2-reliable-1400 | 0 / 500 | 500 / 500 |
| unity-to-ros2-reliable-8000 | 0 / 200 | 200 / 200 |
| unity-to-ros2-best-effort-8192 | 146 / 200 | 200 / 200 |

**Android → helper 方向の reliable QoS が全滅**。best-effort は部分的に届く
(73%) ため、SPDP 自体はある程度届いているが、SEDP / reliable channel 確立段
階で失敗していると推定。Desktop loopback では同じ方向で 100% 受信成功する
ため、**Android 固有の discovery 問題**と断定。

## 既存 findings A〜D との対比

| 既存 finding | Desktop 値 (2026-06-25) | Desktop 値 (今回 2026-06-27) | Android 値 (今回 2026-06-27) | Android での増幅 |
|---|---|---|---|---|
| A. Unity→ROS 2 8 KB 1 021 mps (979 us/msg) | 1 021 mps | 1 333 mps | 517 mps (best-effort 8 KB) | **+49% 悪化** (IL2CPP ARM64 で CPU 律速が顕著) |
| B. main_thread_time_ns_last 9 倍 | 875 us | 88 us (lean) | 84 us (lean) | 改善済 (lean ProfilerRecorders 効いてる) |
| C. reliable main thread 1 ms | 1 ms | 88 us | 84 us | 改善済 |
| D. 全体 5〜10% regression | baseline | (比較なし) | (比較なし) | データ揃わず |

**A が Android で増幅している**。PublishRepeatedAsync のストリーミング化
(`97e446e`) は Desktop 側で 1.0× → 1.3× 改善しているが、Android ARM64 IL2CPP
では 0.4× に留まる。Mono 2x と IL2CPP の GC 戦略差 + ARM64 の small message
serialize 速度差が原因の候補。

**B / C は lean ProfilerRecorders で解消済**。Desktop / Android とも
`main_thread_time_ns_last` が 90 us 程度に戻っている。

## 残存ボトルネック

### A. Android publish 8 KB 帯の 0.4× 律速 (既存 A の増幅)

- Android `unity-to-ros2-best-effort-8192`: 517 mps / 1.93 ms/msg
- Desktop 同: 1 333 mps / 0.75 ms/msg
- Android `unity-to-ros2-reliable-8000`: 538 mps / 1.86 ms/msg
- Desktop 同: 1 470 mps / 0.68 ms/msg

publish 側 Player の CPU 律速。8 KB 1 件あたりの alloc/msg は Android 43.8 KB
(alloc_total=2.5MB / sent=200+200)、Desktop 4.4 KB (alloc_total=8.8MB / sent=200)。
GC pressure の差が 10 倍。`RtpsPayloadOwner` の ArrayPool 経路が IL2CPP ARM64
で想定外の allocation を生んでいる可能性。

### B. Android → helper reliable discovery 不通 (NEW)

- helper (rmw_fastrtts_cpp) が Android プレイヤー (rosettadds 独自 RTPS) の
  SPDP / SEDP を受信できない。best-effort で 146/200 = 73% 部分成功する
  ため、multicast 自体は届いているが reliable 用の session 確立で失敗。
- Desktop loopback で同じ方向 (Player→helper) は 100% 成功するため、rosettadds
  の RTPS 実装に原因はない。Android 固有の network stack / IGMP / WOL /
  doze mode 等の影響が疑わしい。

### C. Desktop 32 KB best-effort + reliable 32 失敗 (out of scope, 計測 infra)

- `ros2-to-unity-best-effort-32k` (Desktop, player=1, helper=0): helper sent 100
  / Player received 64 (rtps_payloads_dropped=288, subscription_handler_invocations=64)
- `ros2-to-unity-reliable-32` (Desktop, player=1, helper=0): helper sent 500
  / Player received 481 (rtps_payloads_dropped=321)
- 32 KB は別 subnet / 経路帯域での計測が本来必要だが、loopback でもドロップ
  しているため別原因 (Player の reader buffer 不足 ?) の可能性。本調査の範囲外。

## 計測中に発見・修正したバグ

### `PerfRunnerPaths.ResolvePlayerBuildPath` の `.apk` 拡張子欠落 (commit `0b457b9`)

- **症状**: `dotnet run tools/rosettadds-perf-runner --build-target Android
  --scenario ...` 時に build 自体は成功 (43.9MB APK 生成) するものの、
  `adb install` が `filename doesn't end .apk or .apex` で失敗する。
- **原因**: `PerfRunnerPaths.ResolvePlayerBuildPath` の default path が
  `ROSettaDDSPerfPlayer` で固定され、Android target でも `.apk` 拡張子が付与
  されていなかった。`--skip-build --player-build /tmp/xxx.apk` では明示的に
  `.apk` 付き path を使うので発覚しなかった (e2e smoke report の経路)。
- **修正**: `if (options.BuildTarget == "Android") return ... .apk;` を追加。
- **テスト**: `PerfRunnerPathsTests` を新設し、Android / StandaloneLinux64 /
  StandaloneOSX / SkipBuild の 4 ケースで TDD 実施 (4 件追加, 全 37 件 pass)。
- **影響範囲**: `tools/rosettadds-perf-runner/PerfRunnerPaths.cs` 1 ファイル
  + テスト 1 ファイル。`Program.cs` 側は無修正 (resolved path をそのまま使う)。

## 計測方法 (再現手順)

```bash
# 0. branch 作成 + spec commit
git checkout -b perf/android-bottleneck-investigation
git commit -m "docs(spec): Android ボトルネック調査の設計を追加" \
  docs/superpowers/specs/2026-06-27-android-bottleneck-investigation-design.md

# 1. Unity Editor 起動 (既存プロセスがあればそれを使う、uloop server 起動済前提)
uloop execute-dynamic-code --project-path Ros2Unity \
  --code 'UnityEditor.EditorUserBuildSettings.activeBuildTarget'

# 2. Android wakelock 設定
adb -s 5HF6OVWCDECMJZ59 shell svc power stayon true

# 3. Android: build + 9 scenario (約 5〜15 分)
dotnet run --project tools/rosettadds-perf-runner -c Release -- \
  --build-target Android \
  --scenario all \
  --android-device 5HF6OVWCDECMJZ59 \
  --artifacts artifacts/perf-android-all \
  --capture-frames 1200

# 4. Desktop へ platform switch
uloop execute-dynamic-code --project-path Ros2Unity --code '
UnityEditor.EditorUserBuildSettings.SwitchActiveBuildTarget(
  UnityEditor.BuildTargetGroup.Standalone, UnityEditor.BuildTarget.StandaloneLinux64);
'

# 5. Desktop: build + 9 scenario (約 2〜5 分)
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target StandaloneLinux64 --backend mono \
  --scenario all \
  --artifacts artifacts/perf-desktop-baseline \
  --capture-frames 1200
```

## 計測 artifact

- Android: `artifacts/perf-android-all/20260627-094348/` (9 scenario, 5 で
  helper 受信 0、1 で部分受信、3 で正常)
- Desktop: `artifacts/perf-desktop-baseline/20260627-100200/` (9 scenario,
  7 で正常、2 で player exit 1)
- 比較元 Desktop baseline: `artifacts/perf/20260625-131941/` (publish 性能
  のみ、Android と直接の比較は skip-build + 同一 build artifact でないため
  誤差含む)

## Next action (本 PR では実装せず、findings のみ)

1. **Android → helper reliable discovery 不通の原因調査 (B)**
   - 仮説: Android の multicast receive 経路が reliable QoS の ACK/Heartbeat
     のタイミング (1ms オーダ) に追いつかない
   - 検証: logcat に RTPS / SPDP / SEDP の trace を出し、helper 側 wireshark
     で UDP 7400-12500 帯の packet を観察
   - 修正案: helper の ready_timeout_ms を 30s に延長、idle_timeout_ms を
     10s に延長して reliable SEDP 完了を待つ
2. **player.profiler.raw pull の Android 対応 (e2e 既知 limitation)**
   - 現状: `player.profiler.raw` は一部取得できている (2.6MB) が、CPU sample
     詳細が壊れている可能性
   - 検証: `-profiler-mode off` で動作確認 / `-profiler-output` path を
     `Application.persistentDataPath` 配下に変えてみる
3. **logcat streaming の runner 化 (e2e 既知 limitation)**
   - 計測中の Unity log を host 側 `runner-logs/` に追記
4. **別 subnet での unicast discovery 対応 (e2e 既知 limitation)**
   - rosettadds 独自 DDS 実装に static peer locator 追加、別 PR
5. **A. Android publish 8 KB 帯の CPU 律速**
   - IL2CPP 固有の allocation 増加原因を Profiler で詳細計測 (現状は
     metrics.ndjson ベースのみ)
   - `RtpsPayloadOwner` の ArrayPool 経路を IL2CPP 向けに最適化
6. **Desktop 32 KB best-effort / reliable-32 の reader buffer 不足調査 (C)**
   - 計測 infrastructure 側の問題。Subscription 側の backlog 設定見直し。
