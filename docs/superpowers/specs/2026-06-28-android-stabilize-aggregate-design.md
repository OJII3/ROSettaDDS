# Android perf-runner preflight 安定化 + multi-run median 集計 (2026-06-28)

## Goal

既存 findings `2026-06-27-android-bottleneck-investigation.md` の Next action 2
「Android run-to-run variance 10× の安定化 (B')」に取り組む。`tools/rosettadds-perf-runner`
に以下 3 機能を追加し、Android 計測の run-to-run variance を縮小する:

1. **`--stabilize-device` フラグ**: 計測前に Android device の状態を軽量リセット
   (WiFi 切断→再接続、screen on wakelock、host への接続待機)
2. **`--repeat N` フラグ**: 各 scenario を N 回連続 run
3. **`--aggregate median` フラグ**: N 回の metrics.ndjson を median 集計して
   manifest.json に追記

複数回 run して median 採用の妥当性検証と、preflight による variance 縮小効果を
findings doc (`docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md`)
で示す。修正は perf-runner のみで完結し、Unity / RTPS / DDS のコードは触らない。

## Background

### 既存 findings (B')

`2026-06-27-android-bottleneck-investigation.md` の計測結果より:

| Scenario | run #1 | run #2 | variance |
|----------|-------:|-------:|---------:|
| ros2-to-unity-best-effort-8192 | 5 116 mps | 1 133 mps | 4.5× 退化 |
| ros2-to-unity-best-effort-32k  |   587 mps |   318 mps | 1.8× 退化 |
| unity-to-ros2-reliable-8000     |   538 mps |  20.8 mps | 24× 退化 (reliable fragmentation 連動) |
| その他の reliable 系            |    1.3〜1.5× |          |          |

Desktop 側は stable なため、**Android 固有の network state / doze mode /
thermal throttling 等の環境要因** が run ごとに効いている可能性大。複数回 run
して median 採用の統計処理、または安定化 (device リブート / WiFi 再接続) が必要。

### 既存 perf-runner の構造

- `tools/rosettadds-perf-runner/Program.cs` の MainAsync で `RunScenario` を
  scenario ごとに 1 回だけ呼び出す
- `AndroidAdbDriver` が adb 経由で install / start / sentinel 待機 / pull を実施
- 各 scenario は `runDir/<scenario-name>/` 配下に metrics.ndjson / player.log 等を保存
- `ArtifactManifest` が `manifest.json` に scenario ごとの path と exit code のみを保存
- 既存の wakelock 設定は `adb shell svc power stayon true` をユーザーが手動で実行
  (計測前 procedure として findings に記載、runner には組み込まれていない)

## Scope

### In scope

- `RunnerOptions` に 3 フラグ追加:
  - `--stabilize-device`: 計測前 device 安定化 (Android のみ有効、Desktop は no-op)
  - `--repeat <N>`: 各 scenario の run 回数 (default 1)
  - `--aggregate <median>`: 集計方法 (default `median`、将来 `mean` 等拡張可能な
    enum として実装)
- `DeviceStabilizer` クラス新設: 軽量 preflight 実装
  - `svc power stayon true` (screen on wakelock)
  - `svc wifi disable` → `svc wifi enable` (WiFi 再接続)
  - `adb shell ping -c 5 -W 2 <host>` で host 接続確認 (最大 30 秒待機)
- `RunAggregator` クラス新設: N 個の metrics.ndjson を読み median 集計
  - 対象メトリクス: `messages_per_second`, `elapsed_ms`, `received`,
    `main_thread_time_ns_last`, `gc_reserved_memory_bytes_last`,
    `gc_used_memory_bytes_last`, `system_used_memory_bytes_last`,
    `serialized_bytes_per_message`
  - 集計結果: `aggregate.json` (新規) + `manifest.json` の ScenarioManifest に追加
- 各 run は独立した `repeat-NN/<scenario-name>/` ディレクトリに保存
  (N 回中の何回目かを明示)
- 既存 `metrics.ndjson` パースは System.Text.Json で NDJSON を行ごと deserialize
- TDD テスト追加:
  - `DeviceStabilizerTests` (FakeAdbClient 利用、cmd 引数アサート)
  - `RunAggregatorTests` (median 計算の unit test、サンプル NDJSON)
  - `RunnerOptionsTests` (新フラグの parse テスト)
  - `ProgramTests` (multi-run loop の integration test、FakeProcessDriver 利用)

### Out of scope

- **Device リブート (`adb reboot`)**: 副作用大 (boot に 30-60 秒、app data 影響)
  で別 PR。本 PR では WiFi 再接続 + wakelock のみ。
- **Permission 自動許可 / cache クリア / battery optimization 解除**: 初回計測時
  のみ必要で、毎 run 行うと副作用大。別 PR。
- **thermal 監視**: 計測中の thermal throttling を自動検出する仕組み。本 PR では
  範囲外。
- **Desktop 側 stabilization**: Desktop は stable なため no-op (将来必要なら同じ
  仕組みで実装可能なよう、`DeviceStabilizer` インターフェースを platform 中立に
  保つ)。
- **mean / min / max / stddev 集計**: `--aggregate median` のみ。enum で将来
  拡張可能な作りにはする。
- **B' 以外の next action** (A / A' / C / helper 詳細解析 / IGMP / unicast):
  別 PR。

## Architecture

```
perf-runner CLI
  --stabilize-device --repeat 5 --aggregate median
       │
       ▼
[ Scenario Loop (既存) ]
       │
       ├─► [ Stabilize Android Device ] ◀──── new
       │   DeviceStabilizer.StabilizeAsync(ct):
       │     - adb shell svc power stayon true
       │     - adb shell svc wifi disable
       │     - sleep 1
       │     - adb shell svc wifi enable
       │     - adb shell ping -c 5 -W 2 <host>
       │     - 失敗時は警告のみで続行
       │
       └─► [ Multi-run Loop (N 回) ] ◀──── new
              for run_idx in 0..N:
                 runDir/<scenario-name>/repeat-{run_idx:D2}/
                   - metrics.ndjson
                   - player.profiler.raw
                   - player.log
                   - helper.stdout.ndjson
                   - helper.stderr.log
                       │
                       ▼
              [ RunAggregator ] ◀──── new
                 - read N × metrics.ndjson
                 - extract "measure_done" event
                 - compute median per metric
                 - write <scenario-name>/aggregate.json
                 - append to manifest.json ScenarioManifest
```

### Components

- **`DeviceStabilizer`** (new): Android 専用の device 状態安定化
  - 依存: `AdbClient` (既存)、`RunnerOptions.HostForPing` (新規オプション)
  - interface は platform-neutral (`DesktopDeviceStabilizer` を no-op で実装
    することで将来 Desktop 対応可能)
- **`RunAggregator`** (new): N 個の metrics.ndjson を median 集計
  - 依存: System.Text.Json (既存)
  - 出力: `aggregate.json` (`scenario-name` 配下)
- **`MetricsParser`** (new): NDJSON を行ごと `JsonDocument` で読み `measure_done`
  event のメトリクスを抽出
- **`RunnerOptions` の拡張**: 3 フラグ + 既存 help / バリデーション整合

### Data Flow (計測 1 scenario × N runs)

```
for run_idx in 0..N:
    if options.StabilizeDevice and options.BuildTarget == "Android":
        await stabilizer.StabilizeAsync(ct)  # 1-3 秒

    scenarioRunDir = runDir / scenario.Name / $"repeat-{run_idx:D2}"
    await RunScenario(scenarioRunDir, ...)  # 既存ロジック

if options.Repeat > 1:
    for each metric in MetricsToAggregate:
        values = [read N metrics.ndjson → extract metric]
        aggregate[metric] = median(values)
    write scenarioRunDir / "aggregate.json"
    append aggregate to manifest.Scenarios[i]
```

### 既存コードとの統合ポイント

- `Program.MainAsync` の scenario loop 内に multi-run loop を追加
- `RunScenario` の `scenarioDir` パラメータを `runDir / "repeat-XX"` に
  変更 (既存コードは `scenarioDir` を受け取るので呼び出し側だけ変更)
- `ArtifactManifest.ScenarioManifest` に `Aggregate` プロパティ追加
  (1 run の時は `null`、N run の時のみ populate)
- `RunnerOptions` に `StabilizeDevice` (bool) / `Repeat` (int) / `Aggregate` (enum)
  追加
- 新規テストファイル:
  - `tools/rosettadds-perf-runner.Tests/DeviceStabilizerTests.cs`
  - `tools/rosettadds-perf-runner.Tests/RunAggregatorTests.cs`
  - `tools/rosettadds-perf-runner.Tests/MetricsParserTests.cs`
  - `tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` (既存ファイルに追記)

## Error Handling

| 失敗ケース | 検出方法 | 対応 |
|------------|----------|------|
| stabilize 失敗 (adb 切断、WiFi off 不可) | adb exit code != 0 | 警告ログのみ、計測は続行 (best-effort) |
| 1 run 失敗 (Player crash / helper timeout) | PlayerExitCode != 0 or HelperExitCode != 0 | 残りの N-1 run で続行、警告ログ、aggregate からは除外 |
| N 全部失敗 | 全 run で exit != 0 | aggregate は `null`、manifest に "all-failed" フラグ、exit code 1 |
| metrics.ndjson 不在 (1 run 中の crash) | File.Exists false | aggregate からは除外、警告ログ |
| measure_done event 不在 (計測が ready で止まった) | JsonDocument parse 失敗 | aggregate からは除外、警告ログ |
| `--repeat 0` または負値 | `ParsePositiveInt` で弾く | 起動時エラー (既存パターン) |
| `--aggregate` 未知値 | enum parse で弾く | 起動時エラー |
| `--stabilize-device` 指定 + BuildTarget != Android | no-op で警告 "stabilize-device is Android-only, ignored for {BuildTarget}" | 計測は続行 |

## Testing (完了条件)

### 自動テスト (TDD)

- [ ] `DeviceStabilizerTests`:
  - `Stabilize_は_svc_power_wakelock_wifi_recycle_ping_の順で_adb_を呼ぶ`
  - `Stabilize_は_ping_成功で完了する` (FakeAdbClient で ping 成功を simulate)
  - `Stabilize_は_ping_失敗時に警告ログを出して_続行する`
  - `Stabilize_Desktop_は_no_op` (DesktopDeviceStabilizer 利用)
- [ ] `RunAggregatorTests`:
  - `Aggregate_Median_は_measure_done_event_から_metrics_を抽出する`
  - `Aggregate_Median_は_奇数個で中央値を返す`
  - `Aggregate_Median_は_偶数個で中央 2 値平均を返す`
  - `Aggregate_1_run_は_その値を返す`
  - `Aggregate_metrics_ndjson_欠損_はスキップして_残りで集計`
- [ ] `MetricsParserTests`:
  - `ParseMeasureDone_は_1_行目を抽出する`
  - `ParseMeasureDone_は_空ファイルで_null_を返す`
  - `ParseMeasureDone_は_不正_JSON_行をスキップする`
- [ ] `RunnerOptionsTests` (既存ファイル追記):
  - `Parse_は_stabilize_device_repeat_5_aggregate_median_を認識する`
  - `Parse_は_repeat_0_で_エラー`
  - `Parse_は_aggregate_unknown_で_エラー`
- [ ] `ProgramTests` (既存ファイル追記):
  - `MainAsync_stabilize_device_指定で_全_scenario_前に_adb_を呼ぶ`
  - `MainAsync_repeat_3_で_各_scenario_を_3_run_する`
  - `MainAsync_repeat_1_は_既存挙動と同じ`

### 計測検証 (実機 / emulator)

- [ ] `--stabilize-device --repeat 5 --aggregate median` で全 9 scenario 完走
- [ ] 既存 run (--repeat 1) と新 run (--repeat 5) で manifest.json の
  scenario 数が一致 (新 run は 1 scenario 1 entry)
- [ ] aggregate.json に全 metric の median 値が出力される
- [ ] preflight ON vs OFF で run-to-run variance が縮小することを確認
  (findings doc で表にして比較)
- [ ] 既存 `dotnet run --project tools/rosettadds-perf-runner` 単独利用の
  シナリオ (--repeat 1 デフォルト) は無変更動作

### 既存テスト回帰

- [ ] `dotnet test tools/rosettadds-perf-runner.Tests` 全 pass
- [ ] `dotnet test tests/rosettadds.Tests` 全 pass (filter で test runner 影響範囲外を確認)

## Steps (実装順)

1. `RunnerOptions` に 3 フラグ追加 (`--stabilize-device`, `--repeat`, `--aggregate`)
2. `DeviceStabilizer` クラス新設 + `DeviceStabilizerTests` を TDD で実装
3. `MetricsParser` クラス新設 + `MetricsParserTests` を TDD で実装
4. `RunAggregator` クラス新設 + `RunAggregatorTests` を TDD で実装
5. `Program.MainAsync` の scenario loop を multi-run + stabilize 対応に改修
6. `ProgramTests` を新フラグ対応に拡張
7. `ArtifactManifest.ScenarioManifest` に `Aggregate` プロパティ追加
8. `dotnet test` 全 pass を確認
9. 実機計測 1 回 (`--stabilize-device --repeat 5 --aggregate median`) で動作確認
10. findings doc 作成 (`docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md`)
11. 1 commit + 1 PR → main マージ

## Validation Gate (PR レビュー観点)

- **後方互換性**: 既存フラグ指定 (`--repeat 1` デフォルト) の挙動が変わらないこと
- **冪等性**: `--stabilize-device` を 1 シナリオ内で複数回呼んでも問題なし
- **テスト網羅**: stabilize / aggregate / multi-run それぞれ 4+ テスト
- **findings**: 既存 run (--repeat 1) と新 run (--repeat 5) の variance 比較表あり
- **再現性**: 計測手順が shell 1 コマンドで完結 (findings doc 内に記載)
- **scope 厳守**: device リブート / permission 許可 / thermal 監視は本 PR に含めない

## Risks

- **WiFi 切断中の計測不可**: `svc wifi disable` 後 `enable` までに数秒、ping
  復旧まで最大 30 秒。計測が 30 秒以上遅延する可能性。タイムアウトを設けて
  ping 失敗時は警告のみで続行
- **計測 artifact 肥大化**: 5 runs × 9 scenarios = 45 subdirectories、各 ~1MB
  合計 ~50MB。`.gitignore` で binary artifact は除外、現状は NDJSON / log のみ
  コミット
- **median の性質**: 偶数個で中央 2 値平均。安定性評価としては outlier に強いが
  、外れ値が 2 つ以上あると central tendency が歪む可能性。本 PR では 5 runs
  想定、外れ値評価は findings で別途議論
- **他 process の WiFi 影響**: stabilize 中に他 process が WiFi を使うと再接続が
  完了しない可能性。計測専用 device であることを前提とする (既存環境と同じ)
- **Android 12+ の permission model**: `svc power stayon true` は Android 12+
  で deprecated の可能性。失敗時は警告のみ (best-effort)

## Next action (本 PR 後)

- **A. Android publish 8 KB 帯の CPU 律速** (Next action 6)
- **A'. Android reliable 8 KB の 71× 退化 (fragmentation コスト)** (Next action 3 in capture investigation)
- **helper 側詳細解析 (reliable 0% vs best-effort 81%)** (Next action 1 in capture investigation)
- **Android 側 IGMP 設定の調査** (Next action 2 in capture investigation)
- **player.profiler.raw pull の Android 対応** (Next action 3 in bottleneck investigation)
- **logcat streaming の runner 化** (Next action 4 in bottleneck investigation)
- **Desktop 32 KB best-effort / reliable-32 の reader buffer 不足調査 (C)** (Next action 7 in bottleneck investigation)
- **別 subnet での unicast discovery 対応** (Next action 5 in bottleneck investigation)
- **Device リブートを含む preflight** (本 PR の out of scope)
