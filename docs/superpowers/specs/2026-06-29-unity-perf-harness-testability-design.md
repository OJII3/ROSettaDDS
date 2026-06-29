# Unity Perf Harness テスト容易化リファクタ

## 背景

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` (282 行) を中心に、Unity Perf Harness のテスト容易性に構造的な課題がある。`docs/superpowers/specs/2026-06-22-perf-revisit-findings.md` 系の一連の findings / design では計測値と計測ノイズの分析が進んだが、計測コード自体の設計品質については以下のギャップが残っている。

- `PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` には EditMode ユニットテストが無い。`[RuntimeInitializeOnLoadMethod]` 経由でしか起動せず、`LoopbackHub` ベースの統合テストも現状 `ROSettaDDSUnityVerificationTests` 経由の派生パスのみで、Perf 計測 event 出力 (`measure_done` ペイロード) の構造を直接 assert できない。
- `PerfMetricsWriter.Escape` などの JSON 純ロジック (`Perf/PerfMetricsWriter.cs:105-112`) に単体テストが無い。`\n` / `\r` / `"` / `\\` の 4 種 escape は手書きで、特殊文字 round-trip の検証がされていない。
- `RunRos2ToUnity` 内の `AddReceiveDiagnostics` / `AddTransportDiagnostics` (`PerfPlayerEntry.cs:184-233`) は `DomainParticipant` / `Subscription<T>` / `UdpTransport` の live 型に直依存し、fake を注入できない。
- `measure_done` event のペイロード構築ロジック (`PerfPlayerEntry.cs:111-117` および `173-180`) が Dictionary 直書きで、field 名のタイポや過剰/不足を EditMode で検出できない。
- `RunUnityToRos2` と `RunRos2ToUnity` の間で `Stopwatch` 初期化パターンや `recorders.Collect()` のタイミングに揺れがあり (`PerfPlayerEntry.cs:106-108` vs `127-137` / `155-160`)、テストでの再現時に差異が出る。

リファクタの主目的は **テスタビリティ向上**。NDJSON event vocabulary、sentinel file 動作、外部 CLI、Runner 側・ROS 2 helper 側とのインターフェースは一切変えない。

## スコープ

`Ros2Unity/Assets/Perf/` 配下のみを変更。`tools/rosettadds-perf-runner/`, `tools/ros2-perf-helper/`, `docs/unity-verification.md` は触らない。

## 変更内容

### 1. `PerfJson` 純関数ヘルパーの新設

`PerfMetricsWriter.cs` の手書き JSON escape を `internal static class PerfJson` に切り出す。

```csharp
internal static class PerfJson
{
    internal static string Escape(string value);
    internal static void WriteString(StringBuilder b, string key, string value, bool first = false);
    internal static void WriteNumber(StringBuilder b, string key, long value, bool first = false);
    internal static void WriteBoolean(StringBuilder b, string key, bool value, bool first = false);
    internal static void WriteValue(StringBuilder b, string key, object value, bool first = false);
}
```

- `Escape` の挙動は **完全不変**: `null` → `""`、`\\` → `\\\\`、`"` → `\\"`、`\n` → `\\n`、`\r` → `\\r` の 4 種のみ。
- `PerfMetricsWriter` はこれらの `PerfJson.*` メソッドを呼び出す薄いラッパになる。`Event` / `WriteSentinel` 等のパブリックな振る舞いは不変。
- 数値フォーマットは `CultureInfo.InvariantCulture` 固定 (現行踏襲)。

### 2. `MeasureDoneBuilder` の新設

`RunUnityToRos2` / `RunRos2ToUnity` 内の `measure_done` event 構築ロジックを 2 つの static メソッドに抽出。

```csharp
internal static class MeasureDoneBuilder
{
    internal static IDictionary<string, object> BuildPublish(
        TimeSpan elapsed,
        int sent,
        int serializedBytesPerMessage,
        IReadOnlyDictionary<string, object> profilerFields);

    internal static IDictionary<string, object> BuildSubscribe(
        TimeSpan elapsed,
        int received,
        int serializedBytesPerMessage,
        IReadOnlyDictionary<string, object> profilerFields,
        IReadOnlyDictionary<string, object> diagnostics);
}
```

- `messages_per_second` の除算保護 (`Math.Max(0.000001d, ...)`) は現行踏襲。
- `serialized_bytes = (long)serializedBytesPerMessage * count` も現行踏襲 (long オーバーフロー保護)。
- `profilerFields` 内の `*_available` / `*_last` / `*_total` / `*_samples` キーはそのまま merge される。

### 3. `PerfDiagnosticsBuilder` と値オブジェクトの追加

`AddReceiveDiagnostics` / `AddTransportDiagnostics` のロジックを builder に切り出す。**`UdpTransport` 型への直接依存を外す**ため、診断データを値オブジェクトとして受ける形に変更。

```csharp
internal readonly struct PerfTransportDiagnostics
{
    public bool Available { get; }
    public long DatagramsReceived { get; }
    public long DatagramsEnqueued { get; }
    public long DatagramsDropped { get; }
    public long DatagramsDispatched { get; }
    public long QueueCount { get; }
}

internal readonly struct PerfSubscriptionDiagnostics
{
    public long PayloadsReceivedFromReader { get; }
    public long MessagesDeserialized { get; }
    public long DeserializeFailures { get; }
    public long HandlerInvocations { get; }
    public long DataSubmessagesReceived { get; }
    public long DataFragSubmessagesReceived { get; }
    public long ReassembledPayloads { get; }
    public long PayloadsDelivered { get; }
    public long PayloadsBufferedPendingMatch { get; }
    public long PayloadsDropped { get; }
}

internal static class PerfDiagnosticsBuilder
{
    internal static IDictionary<string, object> BuildReceive(
        PerfTransportDiagnostics userUnicast,
        PerfTransportDiagnostics userMulticast,
        PerfSubscriptionDiagnostics subscription);
}
```

`RunRos2ToUnity` 側で `participant.UserUnicastTransport as UdpTransport` などのキャスト (`PerfPlayerEntry.cs:220`) を行い、上記 struct に詰めてから `PerfDiagnosticsBuilder.BuildReceive` に渡す。builder は live 型を一切知らない。

`AddReceiveDiagnostics` の 11 個の `subscription_*` / `rtps_*` フィールド名と 10 個の `*_udp_*` / `*_transport_diagnostics_available` フィールド名は不変。

### 4. interface の抽出

`PerfPlayerEntry.Run*` メソッドをテストで fake 注入できるよう、以下の interface を新設。

```csharp
internal interface IPerfMetricsSink : IDisposable
{
    void Event(string name, IDictionary<string, object> fields = null);
    void WriteSentinel(string path, string content);
}

internal interface IPerfProfilerSampler : IDisposable
{
    void Collect();
    IDictionary<string, object> Snapshot();
}

internal interface IPerfClock
{
    IPerfStopwatch Start();
}

internal interface IPerfStopwatch
{
    TimeSpan Elapsed { get; }
    void Stop();
}
```

実装:

| Interface | 実装 | 備考 |
|---|---|---|
| `IPerfMetricsSink` | `PerfMetricsWriter` (改修) | `PerfJson` 経由に書き換える以外、メソッドシグネチャは据え置き |
| `IPerfProfilerSampler` | `PerfProfilerRecorders` (改修) | `Collect` / `Snapshot` は現行のまま |
| `IPerfClock` | `StopwatchPerfClock` (新規) | `Start()` で `Stopwatch.StartNew()` を `StopwatchWrapper` でラップ |

`IPerfParticipantFactory` は導入しない。`RunRos2ToUnity` / `RunUnityToRos2` の EditMode 統合テストでは、既存の `LoopbackHub` + `CustomUserUnicastTransport` パターンで実 `DomainParticipant` を構築し、network 層は in-process 化する (既存 `ROSettaDDSUnityVerificationTests` と同じ流儀)。

### 5. `Run*` メソッドの signature 変更

リファクタ後のシグネチャ:

```csharp
internal static async Task RunUnityToRos2(
    PerfPlayerArguments args,
    DomainParticipant participant,
    IPerfMetricsSink sink,
    IPerfProfilerSampler sampler,
    IPerfClock clock);

internal static async Task RunRos2ToUnity(
    PerfPlayerArguments args,
    DomainParticipant participant,
    IPerfMetricsSink sink,
    IPerfProfilerSampler sampler,
    IPerfClock clock);
```

`PerfPlayerEntry.Run` メソッドは:

1. `PerfPlayerArguments.TryParse` で args 確定 (現行)
2. `var participant = CreateParticipant(args, "player_pub")` で実 participant 構築 (現行)
3. `var sink = new PerfMetricsWriter(args)` (本番) または fake (テスト)
4. `var sampler = PerfProfilerRecorders.StartLean()` / `StartFull()` (本番) または fake (テスト)
5. `var clock = new StopwatchPerfClock()` (本番) または fake (テスト)
6. `await RunUnityToRos2(args, participant, sink, sampler, clock)` で実行

**本番経路の動作は不変** (5 つの引数の中身が違うだけ)。テスト時は 3-5 を fake で差し替える。

### 6. `RunUnityToRos2` / `RunRos2ToUnity` の内部書き換え

両メソッド内の `measure_done` event 構築を `MeasureDoneBuilder.BuildPublish` / `BuildSubscribe` 呼び出しに置換。`RunRos2ToUnity` 内の diagnostics 構築を `PerfDiagnosticsBuilder.BuildReceive` 呼び出しに置換。**出力される NDJSON のキー名・値・順序は完全に不変**。

### 7. 新規 EditMode テストの追加

`Ros2Unity/Assets/Tests/EditMode/` 配下に以下を追加。`ROSettaDDS.UnityVerification.Tests.asmdef` は既存 (Editor only, `ROSettaDDS.UnityPerfHarness` 参照済み) のままで良い。

| ファイル | テスト対象 | 主要ケース |
|---|---|---|
| `PerfJsonTests.cs` | `PerfJson` | Escape 4 種 + null / 空 / 通常、WriteString / WriteNumber / WriteBoolean / WriteValue の出力 |
| `MeasureDoneBuilderTests.cs` | `MeasureDoneBuilder` | 通常系 / `elapsed=0` (除算保護) / profiler フィールド保持 / diagnostics マージ / 空 diagnostics |
| `PerfDiagnosticsBuilderTests.cs` | `PerfDiagnosticsBuilder` | 全 available / 片方 false / subscription 数値反映 / キー完全一致 |
| `PerfHarnessFakes.cs` | `FakePerfMetricsSink` / `FakePerfProfilerSampler` / `FakePerfClock` | events / sentinels / Elapsed の記録 |
| `PerfRunFlowTests.cs` | `PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` 統合 | `LoopbackHub` 経由で measure_done payload 検証 / timeout 経路 / error 経路 |

既存 `PerfHarnessRegressionTests.cs` (63 行、`ProfilerCounterAccumulator` + `AsyncReceiveWaiter` の回帰テスト) は据え置き。

### 8. ファイル一覧 (新規 / 変更)

**新規:**
- `Ros2Unity/Assets/Perf/PerfJson.cs`
- `Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs`
- `Ros2Unity/Assets/Perf/PerfDiagnosticsBuilder.cs`
- `Ros2Unity/Assets/Perf/PerfTransportDiagnostics.cs`
- `Ros2Unity/Assets/Perf/PerfSubscriptionDiagnostics.cs`
- `Ros2Unity/Assets/Perf/IPerfMetricsSink.cs`
- `Ros2Unity/Assets/Perf/IPerfProfilerSampler.cs`
- `Ros2Unity/Assets/Perf/IPerfClock.cs`
- `Ros2Unity/Assets/Perf/IPerfStopwatch.cs`
- `Ros2Unity/Assets/Perf/StopwatchPerfClock.cs`
- `Ros2Unity/Assets/Perf/StopwatchWrapper.cs`
- `Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs`
- `Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs`
- `Ros2Unity/Assets/Tests/EditMode/PerfDiagnosticsBuilderTests.cs`
- `Ros2Unity/Assets/Tests/EditMode/PerfHarnessFakes.cs`
- `Ros2Unity/Assets/Tests/EditMode/PerfRunFlowTests.cs`

**変更:**
- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` (signature 変更 + measure_done 構築を置換)
- `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs` (内部実装を `PerfJson` 呼び出しに置換、`IPerfMetricsSink` 実装に)
- `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` (`IPerfProfilerSampler` 実装に)

`.meta` ファイルも漏れなく追加・更新する (Unity メタファイル方針に従う)。

## 設計上の注意点

- **後方互換**: 公開 API への破壊的変更なし。`PerfPlayerEntry` も `PerfMetricsWriter` も `PerfProfilerRecorders` も `internal` であり、本変更の呼び出し箇所は `PerfPlayerEntry.Run` 内の 1 箇所のみ (各メソッド)。`PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` のシグネチャ変更は `Run` 内の呼び出し元を同時に更新することで吸収する。

- **NDJSON event 互換**: event vocabulary (`start` / `ready` / `matched` / `measure_start` / `measure_done` / `waiting_for_release` / `released` / `done` / `error` / `receive_diagnostics`) と `measure_done` のフィールド名 (`elapsed_ms` / `sent` / `received` / `serialized_bytes_per_message` / `serialized_bytes` / `messages_per_second` / `*_available` / `*_last` / `*_total` / `*_samples` / `*_transport_diagnostics_available` / `*_udp_*` / `subscription_*` / `rtps_*`) は完全互換。Perf 計測 diff で baseline との 0 差分 (timing jitter 除く) を目標とする。

- **`PerfMetricsWriter` の `Debug.Log` 重複出力は維持**: 既存の `Debug.Log(line);` (line 47) は perf run 中の player log 視認性を担保しており、Runner 側 logcat tail の補助線になっているため削除しない。

- **JSON escape の挙動固定**: 4 種 (`\\` / `"` / `\n` / `\r`) のみ置換という現行仕様は意図的。`\t` / `\b` / `\f` / `<` / `>` 等の追加は **しない**。baseline NDJSON との差分を防ぐ。`/u2028` 等の surrogate 対応も **しない** (Unity perf run で payload にサロゲートペアを含めるシナリオが無いため)。

- **`measure_done` の helper 化による出力互換**: `BuildPublish` / `BuildSubscribe` 内の `profilerFields` は **コピーしてから dict 構築**する (呼び出し元の `Snapshot()` 戻り値 Dictionary を mutate しない)。これにより `IPerfProfilerSampler.Snapshot()` を fake 化する際、fake 側の内部状態と helper 戻り値が独立する。

- **`AddReceiveDiagnostics` の削除**: 既存メソッドを `PerfDiagnosticsBuilder.BuildReceive` 呼び出しに置換し、struct 詰め替えは `RunRos2ToUnity` のメソッド本体内で行う。これにより `BuildReceive` は `UdpTransport` も `Subscription<T>` も `DomainParticipant` も一切知らずに済む。

- **既存 perf player ビルドとの互換**: 変更後 `dotnet run --project tools/rosettadds-perf-runner` で `--skip-build` 指定時の出力 NDJSON は現行と完全互換。新規 Player ビルドは IL2CPP / Mono 両 backend で同じ振る舞いをすること。

- **`PerfProfilerMode` / CLI フラグの据え置き**: 既存の `Lean` / `Full` 選択ロジック (`PerfPlayerEntry.cs:47-49`) は維持。`StartLean()` / `StartFull()` の API 不変。

- **スレッド安全性**: `IPerfStopwatch` は single-thread 想定 (呼び出し元が同一スレッドで所有)。`s_exitCode` 静的フィールドの扱いは現行据え置き (1 本エントリ実害なし)。fake のコメントにもこの前提を明記する。

- **`PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` の `internal static` 化**: 現状 `private static` だが、テストからアクセスするため `internal static` に変更。`PerfPlayerEntry` クラス自体はすでに `internal static class` なので変更小。

## 影響範囲

- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` (signature 変更 + 内部書き換え)
- `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs` (内部実装置換 + `IPerfMetricsSink` 実装)
- `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` (`IPerfProfilerSampler` 実装)
- `Ros2Unity/Assets/Perf/` 配下の新規 12 ファイル
- `Ros2Unity/Assets/Tests/EditMode/` 配下の新規 5 ファイル
- `Ros2Unity/Assets/Perf/Properties/AssemblyInfo.cs` (変更なし; 既存 `[InternalsVisibleTo("ROSettaDDS.UnityVerification.Tests")]` で十分)
- 各 `.cs` に対応する `.meta` ファイル (Unity 取り込み要件)

**触らないもの**:
- `tools/rosettadds-perf-runner/` (.NET Runner)
- `tools/ros2-perf-helper/` (C++ helper)
- `docs/unity-verification.md` (外部仕様書)
- 公開 CLI / NDJSON event vocabulary / sentinel file 動作

## 受け入れ基準

- [ ] `dotnet test tools/rosettadds-perf-runner.Tests/` が緑
- [ ] `dotnet test tests/rosettadds.Tests/` が緑
- [ ] Unity EditMode で `ROSettaDDS.UnityVerification.Tests` の既存 + 新規テストが緑 (`PerfJsonTests` 6 ケース / `MeasureDoneBuilderTests` 4 ケース / `PerfDiagnosticsBuilderTests` 4 ケース / `PerfRunFlowTests` 4 ケース 程度を想定)
- [ ] 既存 perf run (`dotnet run --project tools/rosettadds-perf-runner -- --skip-build --player-build <existing> --scenario all`) で出力 NDJSON の構造が現行と完全一致 (event 名・フィールド名・値・順序)
- [ ] baseline NDJSON との diff で 0 差分 (timing jitter 除く)
- [ ] 公開 API への破壊的変更なし
- [ ] `check_unity_meta.sh` クリーン
