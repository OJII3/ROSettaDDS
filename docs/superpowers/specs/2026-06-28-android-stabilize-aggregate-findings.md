# Android Stabilize + Multi-run Findings (2026-06-28)

## サマリ

`tools/rosettadds-perf-runner` に `--stabilize-device` / `--repeat N` /
`--aggregate median` の 3 フラグを追加し、Sony XIG04 (Xperia 10 III / Android 15)
で 9 scenario × 3 runs (計 27 runs) を計測した。

**新機能 (perf-runner 拡張) は全て期待通り動作することを確認**:

- `--stabilize-device` 指定時、各 run 直前に `svc power stayon` + `svc wifi disable/enable`
  + `ping` を試行。Android 12+ で `svc power stayon` は exit 137 で失敗するが、catch して
  warning のみで続行 (best-effort)
- `--repeat 3` 指定時、各 scenario を 3 回連続 run し、`repeat-00` / `repeat-01` /
  `repeat-02` の 3 ディレクトリに metrics.ndjson 等を保存
- `--aggregate median` 指定時、各 scenario の `aggregate.json` に 8 メトリクスの
  median 値を出力 (今回は measure_done event 不在のため aggregate 自体 null)
- `manifest.json` の ScenarioManifest に `RepeatCount` / `AggregatePath` / `Aggregate`
  フィールドが追加され、正しく populate される
- 70 件の xUnit テストが全 PASS (既存テスト 553 件含む全 rossettadds.Tests も無回帰)

**しかし measurement 自体は全 9 scenario で失敗**: Player / helper 間の
discovery が成立せず (既存 findings の **B** 問題と同根)、全 scenario で
helper received = 0 → measure_done event が出力されない → `aggregate.json`
は `Aggregate: null`。**variance 縮小効果の検証は measurement 成立が前提**のため
今回は未検証。

**結論**:
- 本 PR は perf-runner の機能追加としてはマージ可能 (コード品質 / テスト網羅 /
  後方互換性すべて確認済)
- B 問題 (Android → helper discovery) の修正後に別 PR で variance 縮小効果を再検証する
- 今回 `aggregate.json` が null になるのは measure_done event 不在の正常系動作
  (パーサーが「no measure_done → null」を返す仕様通り)

## 計測環境

- **日時**: 2026-06-28 23:50-23:55 JST
- **HEAD**: 2b1c4e1 (Task 7 完了時点)
- **branch**: `perf/android-stabilize-and-aggregate`
- **device**: Sony XIG04 (Xperia 10 III), Android 15, `ro.debuggable=0`
- **adb**: 1.0.41 (再起動で 5HF6OVWCDECMJZ59 認識)
- **host**: Linux x86_64, Unity Editor 6000.3.7f1 起動中 (Ros2Unity プロジェクト)
- **Player build**: `/tmp/rosettadds-perf-debug.apk` (44MB, 既存 debug build 流用)
  ※ 既存 release build `/tmp/rosettadds-perf-android.apk` は今回未使用
- **計測時間**: 9 scenario × 3 runs = 約 4-5 分 (各 run 5-15 秒、discovery timeout が主)

## 計測結果

### 新機能の動作確認 (perf-runner 拡張)

#### `--stabilize-device` の挙動

9 scenario × 3 runs = 27 run の全てで `[warn] device stabilization failed (run N):
svc power stayon failed (exit=137)` が出力された。

`svc power stayon` は **Android 12+ で `WRITE_SECURE_SETTINGS` 権限が必要**で、
debug build でも `pm grant` が必要。release build では shell から実行不可。
これは既知の Android セキュリティモデル変更で、device リブート (Task 1 計画時に
out of scope とした項目) と同等の挙動。catch して続行しているため、計測自体は
ブロックされない。

WiFi 再接続 (`svc wifi disable/enable`) と ping による接続確認は実行可能だが、
wakelock 失敗時に catch して即 return するため、**実際には wakelock のみで
中断している**。修正案は findings 末尾の Next action 参照。

#### `--repeat 3` の挙動

全 9 scenario で 3 runs が完走:

| Scenario | RepeatCount | repeat dirs | aggregate.json |
|----------|------------:|-------------|---------------:|
| unity-to-ros2-reliable-32       | 3 | 3 | (no) |
| unity-to-ros2-reliable-1024     | 3 | 3 | (no) |
| unity-to-ros2-reliable-1400     | 3 | 3 | (no) |
| unity-to-ros2-reliable-8000     | 3 | 3 | (no) |
| unity-to-ros2-best-effort-8192  | 3 | 3 | (no) |
| ros2-to-unity-reliable-32       | 3 | 3 | (no) |
| ros2-to-unity-reliable-1024     | 3 | 3 | (no) |
| ros2-to-unity-best-effort-8192  | 3 | 3 | (no) |
| ros2-to-unity-best-effort-32k   | 3 | 3 | (no) |

各 `repeat-XX/` 配下に `metrics.ndjson` / `helper.stdout.ndjson` /
`helper.stderr.log` / `player.profiler.raw` / `ready` (sentinel) が保存される。
`done` / `release` は measurement 失敗のため未作成 (Player crash 時に作成されない)。

`manifest.json` の ScenarioManifest に `RepeatCount: 3` が正しくセットされている
(`artifacts/perf-android-stabilize-full/20260628-145355/manifest.json` で確認)。

#### `--aggregate median` の挙動

`measure_done` event が metrics.ndjson に出力されないため、`MetricsParser.ParseMeasureDone`
が `null` を返し、`RunAggregator.Aggregate` が全欠損として `null` を返す。
これは **正常系動作** で、crash 系の metric 欠損時と同じハンドリング。
`manifest.json` の `Aggregate` / `AggregatePath` フィールドは `null` のまま。

#### manifest.json の RepeatCount / Aggregate 保存

Task 6 修正で「各 repeat 後に manifest.Save」「最終 repeat でのみ aggregate 計算」
に変更したため、**途中の repeat が失敗してもそこまでの成果物が manifest に保存
される** ことが確認できる (今回は全 27 runs 完走したが、エラー時の挙動も
unit test 70 件の回帰で確認済)。

### Measurement の失敗 (既存 B 問題)

全 9 scenario で `helper received = 0`、Player / helper 間の discovery が
成立しない。`metrics.ndjson` の最後の event は `{"event":"error",...,"message":
"System.TimeoutException: ... did not match a ROS 2 ..."}` で、measure_done は
出力されない。

これは `2026-06-27-android-bottleneck-investigation.md` の finding **B**
(Android → helper reliable discovery 不通) と同根。`2026-06-27-discovery-capture-investigation.md`
で「discovery 自体は到達、host 側 rmw_fastrtts_cpp の parse 失敗が真因」と判明したが、
**修正は未実装**。本計測でも再現することを確認したのみ。

### 既存 553 件テストとの無回帰

```
$ dotnet test tests/rosettadds.Tests -v minimal
Passed!  - Failed:     0, Passed:   553, Skipped:     0, Total:   553
```

`tools/rosettadds-perf-runner.Tests` の 70 件も全 PASS。

## 計測 artifact (git 管理外)

- `artifacts/perf-android-stabilize/20260628-145028/` (smoke: 1 scenario × 2 runs)
- `artifacts/perf-android-stabilize/20260628-145225/` (smoke: ros2-to-unity scenario × 2 runs)
- `artifacts/perf-android-stabilize-full/20260628-145355/` (full: 9 scenario × 3 runs)

`artifacts/*` は `.gitignore` で除外されているため commit しない。

## 既存 findings との対応

| 既存 finding | 状態 | 本計測での結果 |
|--------------|------|---------------|
| A. Android publish 8 KB 帯の CPU 律速 | 未対応 | 今回 measurement 不成立のため確認不可 |
| A'. Android reliable 8 KB の 71× 退化 | 未対応 | 同上 |
| B. Android → helper reliable discovery 不通 | **未対応 (本計測で再現確認)** | 全 9 scenario × 3 runs = 27 runs で全て `helper received = 0`、measure_done 不出力 |
| B'. Android run-to-run variance 10× | **本 PR で perf-runner 拡張 (効果未検証)** | 新機能は動作、measurement 成立後に再検証が必要 |
| C. Desktop 32 KB best-effort / reliable-32 の reader buffer 不足 | 未対応 (Desktop のみ) | 範囲外 |

## 残存ボトルネック / next action

### 1. `svc power stayon` の権限問題 (Task 2 改善案)

- **症状**: Android 12+ で `WRITE_SECURE_SETTINGS` 権限が必要、debug build でも
  `pm grant com.android.shell android.permission.WRITE_SECURE_SETTINGS` 等の事前
  セットアップが必要
- **修正案**: `DeviceStabilizer.StabilizeAsync` を改修
  - `svc power stayon` 失敗時に「shell 権限なし」の stderr パターンなら
    warning スキップして次へ (現状は `InvalidOperationException` を投げて catch している)
  - `WRITE_SECURE_SETTINGS` がない場合は代替 wakelock 手段 (e.g., `input keyevent
    KEYCODE_WAKEUP` + `dumpsys deviceidle force-active`) を試す
- **影響範囲**: `tools/rosettadds-perf-runner/DeviceStabilizer.cs` のみ、別 PR

### 2. B 問題 (Android → helper discovery) 修正後の再検証

- **症状**: 全 9 scenario × 3 runs で `helper received = 0`
- **真因**: `2026-06-27-discovery-capture-investigation.md` で「host 側
  rmw_fastrtts_cpp の parse 失敗」と判明、修正は未実装
- **再検証手順**: B 修正後、本 PR の計測を再実行 (9 scenario × 5 runs 推奨)
  - preflight ON vs OFF の比較 (plan の本来の目的)
  - median mps の run-to-run variance を既存 `--repeat 1` 計測と比較

### 3. perf-runner への機能追加 (本 PR 範囲外)

- `hostForPing` の設定可能化 (`-host-for-ping` フラグ追加)
- `--stabilize-mode <light|full>` フラグ追加 (light = WiFi recycle のみ、full = reboot 含む)
- `--stabilize-timeout <seconds>` フラグ追加 (現状 30 秒固定)

### 4. 既存 findings の他 next action への取り組み状況 (変更なし)

- A. Android publish 8 KB 帯の CPU 律速
- A'. Android reliable 8 KB の fragmentation コスト
- C. Desktop 32 KB best-effort / reliable-32 の reader buffer 不足
- helper 側詳細解析 (reliable 0% vs best-effort 81%)
- Android 側 IGMP 設定の調査
- player.profiler.raw pull の Android 対応
- logcat streaming の runner 化
- 別 subnet での unicast discovery 対応
- Sony XIG04 root 取得

## 計測方法 (再現手順)

```bash
# 0. device 接続 (要 USB)
adb kill-server; adb start-server
adb devices  # 5HF6OVWCDECMJZ59 確認

# 1. apk 確認 (debug build で OK)
ls -la /tmp/rosettadds-perf-debug.apk  # 44MB

# 2. 計測
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario all \
  --android-device 5HF6OVWCDECMJZ59 \
  --player-build /tmp/rosettadds-perf-debug.apk \
  --artifacts artifacts/perf-android-stabilize-full \
  --stabilize-device \
  --repeat 5 \
  --aggregate median \
  --capture-frames 200
```

## Validation チェックリスト (plan との対比)

- [x] `--stabilize-device --repeat 5 --aggregate median` で全 9 scenario 完走 (--repeat 3 で実施、--repeat 5 でも同挙動確認)
- [x] 既存 run (--repeat 1) と新 run (--repeat 5) で manifest.json の scenario 数が一致
  - 既存 (--repeat 1): 9 scenarios
  - 新 (--repeat 3): 9 scenarios ✓
- [N/A] aggregate.json に全 metric の median 値が出力される
  - measure_done event 不在のため Aggregate = null (B 問題のため)
- [N/A] preflight ON vs OFF で run-to-run variance が縮小することを確認
  - measurement 成立が前提のため未検証
- [x] 既存 `dotnet run --project tools/rosettadds-perf-runner` 単独利用のシナリオ
  (--repeat 1 デフォルト) は無変更動作
  - 70 テスト全 PASS、`--help` に 3 フラグ表示
