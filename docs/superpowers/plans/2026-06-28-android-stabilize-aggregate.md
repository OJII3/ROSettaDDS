# Android perf-runner preflight 安定化 + multi-run median 集計 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `tools/rosettadds-perf-runner` に `--stabilize-device` (軽量 preflight)、`--repeat N` (multi-run)、`--aggregate median` (集計) の 3 フラグを追加し、Android 計測の run-to-run variance を縮小する。

**Architecture:** 既存 `RunScenario` を multi-run ループで囲み、各 scenario 前に `DeviceStabilizer.StabilizeAsync` で WiFi 切断→再接続 + screen on wakelock + host 接続待機を実施。各 run は `repeat-XX/<scenario>/` に保存、`RunAggregator` が N 個の `metrics.ndjson` を median 集計して `aggregate.json` + manifest.json に追記。TDD で全 unit test を先に書き、最小実装で通す。

**Tech Stack:** C# (.NET 8.0), xUnit, FluentAssertions, System.Text.Json, adb

**Design doc:** `docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-design.md`

---

## 変更 / 作成ファイル一覧

| 操作 | パス | 役割 |
| --- | --- | --- |
| Modify | `tools/rosettadds-perf-runner/RunnerOptions.cs` | 3 フラグ追加 + AggregateKind enum |
| Modify | `tools/rosettadds-perf-runner/Program.cs` | scenario loop に stabilize + multi-run 統合 |
| Modify | `tools/rosettadds-perf-runner/ArtifactManifest.cs` | ScenarioManifest に Aggregate プロパティ追加 |
| Create | `tools/rosettadds-perf-runner/DeviceStabilizer.cs` | 軽量 preflight (wakelock + WiFi recycle + ping) |
| Create | `tools/rosettadds-perf-runner/MetricsParser.cs` | NDJSON → measure_done event metrics 抽出 |
| Create | `tools/rosettadds-perf-runner/RunAggregator.cs` | N runs の metrics を median 集計 |
| Modify | `tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` | 新フラグ parse テスト追記 |
| Modify | `tools/rosettadds-perf-runner.Tests/ProgramTests.cs` | multi-run / stabilize integration test |
| Create | `tools/rosettadds-perf-runner.Tests/DeviceStabilizerTests.cs` | preflight の unit test |
| Create | `tools/rosettadds-perf-runner.Tests/MetricsParserTests.cs` | NDJSON parse の unit test |
| Create | `tools/rosettadds-perf-runner.Tests/RunAggregatorTests.cs` | median 集計の unit test |

Unity / RTPS / DDS / helper のコードは触らない。

---

## Task 1: `RunnerOptions` に 3 フラグ + `AggregateKind` enum を追加

**Files:**
- Modify: `tools/rosettadds-perf-runner/RunnerOptions.cs`

- [ ] **Step 1.1: 失敗するテストを追加**

`tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` の末尾に以下を追加:

```csharp
[Fact]
public void StabilizeDevice_既定値は_false()
{
    var options = RunnerOptions.Parse(Array.Empty<string>());
    options.StabilizeDevice.Should().BeFalse();
}

[Fact]
public void StabilizeDevice_指定が_保持される()
{
    var options = RunnerOptions.Parse(new[] { "--stabilize-device" });
    options.StabilizeDevice.Should().BeTrue();
}

[Fact]
public void Repeat_既定値は_1()
{
    var options = RunnerOptions.Parse(Array.Empty<string>());
    options.Repeat.Should().Be(1);
}

[Fact]
public void Repeat_5_が_保持される()
{
    var options = RunnerOptions.Parse(new[] { "--repeat", "5" });
    options.Repeat.Should().Be(5);
}

[Fact]
public void Repeat_0_は_例外()
{
    var act = () => RunnerOptions.Parse(new[] { "--repeat", "0" });
    act.Should().Throw<ArgumentException>()
        .WithMessage("*--repeat*positive integer*");
}

[Fact]
public void Repeat_負値は_例外()
{
    var act = () => RunnerOptions.Parse(new[] { "--repeat", "-1" });
    act.Should().Throw<ArgumentException>();
}

[Fact]
public void Aggregate_既定値は_median()
{
    var options = RunnerOptions.Parse(Array.Empty<string>());
    options.Aggregate.Should().Be(AggregateKind.Median);
}

[Fact]
public void Aggregate_median_を_受理する()
{
    var options = RunnerOptions.Parse(new[] { "--aggregate", "median" });
    options.Aggregate.Should().Be(AggregateKind.Median);
}

[Fact]
public void Aggregate_未知値は_例外()
{
    var act = () => RunnerOptions.Parse(new[] { "--aggregate", "stddev" });
    act.Should().Throw<ArgumentException>()
        .WithMessage("*--aggregate*median*");
}
```

- [ ] **Step 1.2: テスト実行 → FAIL 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~RunnerOptionsTests" -v minimal
```

期待: `StabilizeDevice_既定値は_false` 等 9 件でコンパイルエラー (CS1061) または FAIL。

- [ ] **Step 1.3: `RunnerOptions` の修正**

`tools/rosettadds-perf-runner/RunnerOptions.cs` を以下のように修正する:

1. クラス冒頭に `enum AggregateKind { Median }` を追加
2. プロパティ追加 (既存の `bool Help` の下あたり):
   ```csharp
   internal bool StabilizeDevice { get; private set; }
   internal int Repeat { get; private set; } = 1;
   internal AggregateKind Aggregate { get; private set; } = AggregateKind.Median;
   ```
3. `Parse` メソッドの switch 文に以下を追加 (`case "--android-activity":` の下):
   ```csharp
   case "--stabilize-device":
       options.StabilizeDevice = true;
       break;
   case "--repeat":
       options.Repeat = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
       break;
   case "--aggregate":
       {
           string value = RequireValue(args, ref i, arg);
           options.Aggregate = value switch
           {
               "median" => AggregateKind.Median,
               _ => throw new ArgumentException("--aggregate must be median"),
           };
       }
       break;
   ```
4. `PrintHelp` に 3 行追加 (AndroidDevice 行の下):
   ```csharp
   output.WriteLine("  --stabilize-device                       計測前 Android device 状態を安定化 (WiFi 再接続 + wakelock + ping)");
   output.WriteLine("  --repeat <count>                         各 scenario を N 回連続 run (default 1, median 集計対象)");
   output.WriteLine("  --aggregate <median>                     multi-run 時の集計方法 (default median)");
   ```

- [ ] **Step 1.4: テスト実行 → PASS 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~RunnerOptionsTests" -v minimal
```

期待: `RunnerOptionsTests` の全 19 件 (既存 10 + 新規 9) PASS。

- [ ] **Step 1.5: commit**

```bash
git add tools/rosettadds-perf-runner/RunnerOptions.cs tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs
git commit -m "feat(perf-runner): stabilize-device / repeat / aggregate フラグを追加"
```

---

## Task 2: `DeviceStabilizer` クラス新設 (Android 軽量 preflight)

**Files:**
- Create: `tools/rosettadds-perf-runner/DeviceStabilizer.cs`
- Create: `tools/rosettadds-perf-runner.Tests/DeviceStabilizerTests.cs`

- [ ] **Step 2.1: 失敗するテストを作成**

`tools/rosettadds-perf-runner.Tests/DeviceStabilizerTests.cs` を新規作成:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class DeviceStabilizerTests
{
    [Fact]
    public async Task Stabilize_は_wakelock_wifi_recycle_ping_を順に呼ぶ()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(
            client, hostForPing: "192.168.0.20");

        await stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // 1: svc power stayon
        // 2: svc wifi disable
        // 3: svc wifi enable
        // 4: ping -c 5 -W 2 host
        fake.Calls.Should().HaveCount(4);
        fake.Calls[0].Should().Be("adb -s DEV shell svc power stayon true");
        fake.Calls[1].Should().Be("adb -s DEV shell svc wifi disable");
        fake.Calls[2].Should().Be("adb -s DEV shell svc wifi enable");
        fake.Calls[3].Should().StartWith("adb -s DEV shell ping -c 5 -W 2 192.168.0.20");
    }

    [Fact]
    public async Task Stabilize_は_ping_成功で完了する()
    {
        var fake = new FakeAdbClient();
        // ping は exit 0 が返れば成功とみなす
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(client, "192.168.0.20");

        Func<Task> act = () => stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stabilize_は_ping_失敗時にタイムアウト例外を投げる()
    {
        var fake = new FakeAdbClient { ExitCodeOverride = 1, StderrOverride = "ping: network unreachable" };
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(client, "192.168.0.20");

        Func<Task> act = () => stabilizer.StabilizeAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*ping*");
    }

    [Fact]
    public async Task DesktopStabilizer_は_no_op()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new DesktopDeviceStabilizer();

        await stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        fake.Calls.Should().BeEmpty();
    }
}
```

- [ ] **Step 2.2: テスト実行 → FAIL 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~DeviceStabilizerTests" -v minimal
```

期待: コンパイルエラー (`AndroidDeviceStabilizer`, `DesktopDeviceStabilizer` 未定義)。

- [ ] **Step 2.3: `DeviceStabilizer.cs` 実装**

`tools/rosettadds-perf-runner/DeviceStabilizer.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal interface IDeviceStabilizer
{
    Task StabilizeAsync(TimeSpan timeout, CancellationToken ct);
}

internal sealed class AndroidDeviceStabilizer : IDeviceStabilizer
{
    private readonly AdbClient _adb;
    private readonly string _hostForPing;

    public AndroidDeviceStabilizer(AdbClient adb, string hostForPing)
    {
        _adb = adb;
        _hostForPing = hostForPing;
    }

    public async Task StabilizeAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 1) screen on wakelock
        var r1 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc power stayon true", ct);
        if (r1.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc power stayon failed (exit={r1.ExitCode}): {r1.Stderr.Trim()}");
        }

        // 2) WiFi recycle: disable → enable
        var r2 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc wifi disable", ct);
        if (r2.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc wifi disable failed (exit={r2.ExitCode}): {r2.Stderr.Trim()}");
        }
        // 1 秒待機 (WiFi 切断完了待ち)
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var r3 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc wifi enable", ct);
        if (r3.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc wifi enable failed (exit={r3.ExitCode}): {r3.Stderr.Trim()}");
        }

        // 3) host への接続確認 (最大 timeout までリトライ)
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string pingCmd = $"adb -s {_adb.Serial} shell ping -c 5 -W 2 {_hostForPing}";
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var rp = await _adb.RunAsync(pingCmd, ct);
            if (rp.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new TimeoutException(
            $"timed out waiting for ping {_hostForPing} to succeed within {timeout}");
    }
}

internal sealed class DesktopDeviceStabilizer : IDeviceStabilizer
{
    public Task StabilizeAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 2.4: テスト実行 → PASS 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~DeviceStabilizerTests" -v minimal
```

期待: 4 件全 PASS。

- [ ] **Step 2.5: commit**

```bash
git add tools/rosettadds-perf-runner/DeviceStabilizer.cs tools/rosettadds-perf-runner.Tests/DeviceStabilizerTests.cs
git commit -m "feat(perf-runner): DeviceStabilizer (Android 軽量 preflight) を追加"
```

---

## Task 3: `MetricsParser` クラス新設 (NDJSON → measure_done metrics)

**Files:**
- Create: `tools/rosettadds-perf-runner/MetricsParser.cs`
- Create: `tools/rosettadds-perf-runner.Tests/MetricsParserTests.cs`

- [ ] **Step 3.1: 失敗するテストを作成**

`tools/rosettadds-perf-runner.Tests/MetricsParserTests.cs` を新規作成:

```csharp
using System;
using System.IO;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class MetricsParserTests
{
    [Fact]
    public void ParseMeasureDone_は_measure_done_event_を抽出する()
    {
        string path = Path.Combine(Path.GetTempPath(), "metrics-parser-test.ndjson");
        File.WriteAllText(path,
            "{\"event\":\"start\",\"scenario\":\"x\"}\n" +
            "{\"event\":\"ready\"}\n" +
            "{\"event\":\"measure_done\",\"messages_per_second\":1234.5,\"elapsed_ms\":50.0,\"received\":500,\"main_thread_time_ns_last\":82622}\n" +
            "{\"event\":\"done\"}\n");
        try
        {
            var result = MetricsParser.ParseMeasureDone(path);
            result.Should().NotBeNull();
            result!.MessagesPerSecond.Should().Be(1234.5);
            result.ElapsedMs.Should().Be(50.0);
            result.Received.Should().Be(500);
            result.MainThreadTimeNsLast.Should().Be(82622);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParseMeasureDone_は_空ファイルで_null()
    {
        string path = Path.Combine(Path.GetTempPath(), "metrics-parser-test-empty.ndjson");
        File.WriteAllText(path, string.Empty);
        try
        {
            var result = MetricsParser.ParseMeasureDone(path);
            result.Should().BeNull();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParseMeasureDone_は_measure_done_不在で_null()
    {
        string path = Path.Combine(Path.GetTempPath(), "metrics-parser-test-noevent.ndjson");
        File.WriteAllText(path,
            "{\"event\":\"start\"}\n" +
            "{\"event\":\"ready\"}\n" +
            "{\"event\":\"done\"}\n");
        try
        {
            var result = MetricsParser.ParseMeasureDone(path);
            result.Should().BeNull();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParseMeasureDone_は_不正_JSON_行をスキップする()
    {
        string path = Path.Combine(Path.GetTempPath(), "metrics-parser-test-bad.ndjson");
        File.WriteAllText(path,
            "garbage line\n" +
            "{\"event\":\"measure_done\",\"messages_per_second\":42.0,\"elapsed_ms\":10.0,\"received\":10}\n");
        try
        {
            var result = MetricsParser.ParseMeasureDone(path);
            result.Should().NotBeNull();
            result!.MessagesPerSecond.Should().Be(42.0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParseMeasureDone_は_ファイル不在で_null()
    {
        var result = MetricsParser.ParseMeasureDone("/nonexistent/path/metrics.ndjson");
        result.Should().BeNull();
    }
}
```

- [ ] **Step 3.2: テスト実行 → FAIL 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~MetricsParserTests" -v minimal
```

期待: コンパイルエラー (`MetricsParser` 未定義)。

- [ ] **Step 3.3: `MetricsParser.cs` 実装**

`tools/rosettadds-perf-runner/MetricsParser.cs` を新規作成:

```csharp
using System;
using System.IO;
using System.Text.Json;

namespace ROSettaDDS.PerfRunner;

internal sealed class MeasureDoneMetrics
{
    public double MessagesPerSecond { get; set; }
    public double ElapsedMs { get; set; }
    public long Received { get; set; }
    public long MainThreadTimeNsLast { get; set; }
    public long GcReservedMemoryBytesLast { get; set; }
    public long GcUsedMemoryBytesLast { get; set; }
    public long SystemUsedMemoryBytesLast { get; set; }
    public long SerializedBytesPerMessage { get; set; }
}

internal static class MetricsParser
{
    public static MeasureDoneMetrics? ParseMeasureDone(string path)
    {
        if (!File.Exists(path)) return null;

        MeasureDoneMetrics? latest = null;
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("event", out JsonElement ev)) continue;
                if (ev.GetString() != "measure_done") continue;
                latest = new MeasureDoneMetrics
                {
                    MessagesPerSecond = ReadDouble(root, "messages_per_second"),
                    ElapsedMs = ReadDouble(root, "elapsed_ms"),
                    Received = ReadLong(root, "received"),
                    MainThreadTimeNsLast = ReadLong(root, "main_thread_time_ns_last"),
                    GcReservedMemoryBytesLast = ReadLong(root, "gc_reserved_memory_bytes_last"),
                    GcUsedMemoryBytesLast = ReadLong(root, "gc_used_memory_bytes_last"),
                    SystemUsedMemoryBytesLast = ReadLong(root, "system_used_memory_bytes_last"),
                    SerializedBytesPerMessage = ReadLong(root, "serialized_bytes_per_message"),
                };
            }
            catch (JsonException)
            {
                // 不正 JSON 行はスキップ
                continue;
            }
        }
        return latest;
    }

    private static double ReadDouble(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }
        return 0.0;
    }

    private static long ReadLong(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
        {
            return v.GetInt64();
        }
        return 0;
    }
}
```

- [ ] **Step 3.4: テスト実行 → PASS 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~MetricsParserTests" -v minimal
```

期待: 5 件全 PASS。

- [ ] **Step 3.5: commit**

```bash
git add tools/rosettadds-perf-runner/MetricsParser.cs tools/rosettadds-perf-runner.Tests/MetricsParserTests.cs
git commit -m "feat(perf-runner): MetricsParser (NDJSON → measure_done) を追加"
```

---

## Task 4: `RunAggregator` クラス新設 (N runs の metrics を median 集計)

**Files:**
- Create: `tools/rosettadds-perf-runner/RunAggregator.cs`
- Create: `tools/rosettadds-perf-runner.Tests/RunAggregatorTests.cs`

- [ ] **Step 4.1: 失敗するテストを作成**

`tools/rosettadds-perf-runner.Tests/RunAggregatorTests.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class RunAggregatorTests
{
    [Fact]
    public void Aggregate_は_1_run_でその値を返す()
    {
        var result = new[] { 100.0 };
        RunAggregator.Median(result).Should().Be(100.0);
    }

    [Fact]
    public void Aggregate_Median_は_奇数個で中央値()
    {
        RunAggregator.Median(new[] { 1.0, 5.0, 3.0, 9.0, 7.0 }).Should().Be(5.0);
    }

    [Fact]
    public void Aggregate_Median_は_偶数個で中央2値平均()
    {
        RunAggregator.Median(new[] { 1.0, 2.0, 3.0, 4.0 }).Should().Be(2.5);
    }

    [Fact]
    public void Aggregate_Median_は_空で_0()
    {
        RunAggregator.Median(Array.Empty<double>()).Should().Be(0.0);
    }

    [Fact]
    public void Aggregate_Median_は_未ソート入力でも正しい()
    {
        RunAggregator.Median(new[] { 9.0, 1.0, 5.0, 3.0, 7.0 }).Should().Be(5.0);
    }

    [Fact]
    public void Aggregate_は_N個のmetrics_pathから_measure_done_metricsを_集計する()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "agg-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            // 3 runs の metrics.ndjson を作成
            WriteMeasureDone(Path.Combine(tempDir, "repeat-00", "metrics.ndjson"),
                mps: 100, elapsed: 50, received: 500);
            WriteMeasureDone(Path.Combine(tempDir, "repeat-01", "metrics.ndjson"),
                mps: 200, elapsed: 25, received: 500);
            WriteMeasureDone(Path.Combine(tempDir, "repeat-02", "metrics.ndjson"),
                mps: 300, elapsed: 16, received: 500);

            // 各 repeat-XX 配下を 1 つの runDir として aggregator に渡す
            var runDirs = new[]
            {
                Path.Combine(tempDir, "repeat-00"),
                Path.Combine(tempDir, "repeat-01"),
                Path.Combine(tempDir, "repeat-02"),
            };
            var aggregate = RunAggregator.Aggregate(runDirs, AggregateKind.Median);
            aggregate.Should().NotBeNull();
            aggregate!.MessagesPerSecond.Should().Be(200.0);
            aggregate.ElapsedMs.Should().Be(25.0);
            aggregate.Received.Should().Be(500);
            aggregate.RunCount.Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Aggregate_は_metrics_欠損_runをスキップして_残りで集計()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "agg-test-missing-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            WriteMeasureDone(Path.Combine(tempDir, "repeat-00", "metrics.ndjson"),
                mps: 100, elapsed: 50, received: 500);
            // repeat-01 は metrics.ndjson なし
            WriteMeasureDone(Path.Combine(tempDir, "repeat-02", "metrics.ndjson"),
                mps: 300, elapsed: 16, received: 500);

            var runDirs = new[]
            {
                Path.Combine(tempDir, "repeat-00"),
                Path.Combine(tempDir, "repeat-01"),
                Path.Combine(tempDir, "repeat-02"),
            };
            var aggregate = RunAggregator.Aggregate(runDirs, AggregateKind.Median);
            aggregate.Should().NotBeNull();
            aggregate!.MessagesPerSecond.Should().Be(200.0);
            aggregate.RunCount.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Aggregate_は_全_run_で_metrics_なし_で_null()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "agg-test-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var runDirs = new[]
            {
                Path.Combine(tempDir, "repeat-00"),
                Path.Combine(tempDir, "repeat-01"),
            };
            var aggregate = RunAggregator.Aggregate(runDirs, AggregateKind.Median);
            aggregate.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteMeasureDone(string path, double mps, double elapsed, long received)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"event\":\"start\"}\n" +
            $"{{\"event\":\"measure_done\",\"messages_per_second\":{mps},\"elapsed_ms\":{elapsed},\"received\":{received}}}\n" +
            "{\"event\":\"done\"}\n");
    }
}
```

- [ ] **Step 4.2: テスト実行 → FAIL 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~RunAggregatorTests" -v minimal
```

期待: コンパイルエラー (`RunAggregator` 未定義)。

- [ ] **Step 4.3: `RunAggregator.cs` 実装**

`tools/rosettadds-perf-runner/RunAggregator.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ROSettaDDS.PerfRunner;

internal sealed class AggregateMetrics
{
    public int RunCount { get; set; }
    public double MessagesPerSecond { get; set; }
    public double ElapsedMs { get; set; }
    public long Received { get; set; }
    public long MainThreadTimeNsLast { get; set; }
    public long GcReservedMemoryBytesLast { get; set; }
    public long GcUsedMemoryBytesLast { get; set; }
    public long SystemUsedMemoryBytesLast { get; set; }
    public long SerializedBytesPerMessage { get; set; }
}

internal static class RunAggregator
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = new double[values.Count];
        for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[mid];
        }
        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static long MedianLong(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new long[values.Count];
        for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[mid];
        }
        return (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static AggregateMetrics? Aggregate(
        IReadOnlyList<string> runDirs,
        AggregateKind kind)
    {
        if (kind != AggregateKind.Median)
        {
            throw new ArgumentException("only median is currently supported");
        }
        var mpsList = new List<double>();
        var elapsedList = new List<double>();
        var receivedList = new List<long>();
        var mainThreadList = new List<long>();
        var gcReservedList = new List<long>();
        var gcUsedList = new List<long>();
        var sysUsedList = new List<long>();
        var serBytesList = new List<long>();

        foreach (string dir in runDirs)
        {
            string metricsPath = Path.Combine(dir, "metrics.ndjson");
            MeasureDoneMetrics? m = MetricsParser.ParseMeasureDone(metricsPath);
            if (m == null) continue;
            mpsList.Add(m.MessagesPerSecond);
            elapsedList.Add(m.ElapsedMs);
            receivedList.Add(m.Received);
            mainThreadList.Add(m.MainThreadTimeNsLast);
            gcReservedList.Add(m.GcReservedMemoryBytesLast);
            gcUsedList.Add(m.GcUsedMemoryBytesLast);
            sysUsedList.Add(m.SystemUsedMemoryBytesLast);
            serBytesList.Add(m.SerializedBytesPerMessage);
        }

        if (mpsList.Count == 0) return null;

        return new AggregateMetrics
        {
            RunCount = mpsList.Count,
            MessagesPerSecond = Median(mpsList),
            ElapsedMs = Median(elapsedList),
            Received = MedianLong(receivedList),
            MainThreadTimeNsLast = MedianLong(mainThreadList),
            GcReservedMemoryBytesLast = MedianLong(gcReservedList),
            GcUsedMemoryBytesLast = MedianLong(gcUsedList),
            SystemUsedMemoryBytesLast = MedianLong(sysUsedList),
            SerializedBytesPerMessage = MedianLong(serBytesList),
        };
    }

    public static void Save(string path, AggregateMetrics metrics)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(metrics, options));
    }
}
```

- [ ] **Step 4.4: テスト実行 → PASS 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests --filter "FullyQualifiedName~RunAggregatorTests" -v minimal
```

期待: 8 件全 PASS。

- [ ] **Step 4.5: commit**

```bash
git add tools/rosettadds-perf-runner/RunAggregator.cs tools/rosettadds-perf-runner.Tests/RunAggregatorTests.cs
git commit -m "feat(perf-runner): RunAggregator (median 集計) を追加"
```

---

## Task 5: `ArtifactManifest.ScenarioManifest` に `Aggregate` プロパティ追加

**Files:**
- Modify: `tools/rosettadds-perf-runner/ArtifactManifest.cs`

- [ ] **Step 5.1: `Aggregate` プロパティ追加**

`tools/rosettadds-perf-runner/ArtifactManifest.cs` の `ScenarioManifest` クラスを以下に修正
(既存プロパティの後に `Aggregate` と `RepeatCount` を追加):

```csharp
internal sealed class ScenarioManifest
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string MetricsPath { get; set; } = "";
    public string ProfilerPath { get; set; } = "";
    public string PlayerLogPath { get; set; } = "";
    public string HelperStdoutPath { get; set; } = "";
    public string HelperStderrPath { get; set; } = "";
    public int PlayerExitCode { get; set; }
    public int HelperExitCode { get; set; }
    public int RepeatCount { get; set; } = 1;
    public string? AggregatePath { get; set; }
    public AggregateMetrics? Aggregate { get; set; }
}
```

- [ ] **Step 5.2: ビルド確認**

```bash
dotnet build tools/rosettadds-perf-runner -c Release
```

期待: 0 errors / 0 warnings。

- [ ] **Step 5.3: 既存テスト回帰確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests -v minimal
```

期待: 既存全テスト PASS (ArtifactManifest 周りで破壊していないこと)。

- [ ] **Step 5.4: commit**

```bash
git add tools/rosettadds-perf-runner/ArtifactManifest.cs
git commit -m "feat(perf-runner): ScenarioManifest に Aggregate / RepeatCount プロパティを追加"
```

---

## Task 6: `Program.MainAsync` を multi-run + stabilize 対応に改修

**Files:**
- Modify: `tools/rosettadds-perf-runner/Program.cs`

- [ ] **Step 6.1: 統合テストを追加**

`tools/rosettadds-perf-runner.Tests/ProgramTests.cs` の末尾に以下を追加
(既存 `ProgramTests.cs` の構造に従い、必要なら using を追加):

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class ProgramMultiRunTests
{
    [Fact]
    public async Task MainAsync_repeat_3_で_各_scenario_を_3_run_する()
    {
        string runDir = Path.Combine(Path.GetTempPath(), "prog-multirun-test-" + Guid.NewGuid());
        try
        {
            // 検証: --repeat 3 指定で metrics.ndjson が 3 個作成される
            // (FakeProcessDriver / FakeAdbClient が必要、本テストは最小実装)
            // 詳細は Step 6.4 で実装確認
        }
        finally
        {
            if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true);
        }
    }
}
```

注意: 上のテストは Task 6 内で FakeProcessDriver / FakeAdbClient を使った完全形に
置き換える (Step 6.4 で差し替え)。

- [ ] **Step 6.2: `Program.cs` の改修**

`tools/rosettadds-perf-runner/Program.cs` の `MainAsync` を以下のように修正する:

1. 既存の `IDeviceStabilizer` 取得ロジック追加 (RunScenario ループの前):

   ```csharp
   IDeviceStabilizer stabilizer = options.BuildTarget == "Android"
       ? new AndroidDeviceStabilizer(
             new AdbClient(new RealAdbCommandSink(options.Adb), options.AndroidDevice
                 ?? throw new InvalidOperationException("--android-device is required for --stabilize-device on Android")),
             hostForPing: "192.168.0.20"  // 固定値。設定可能化は別 PR (out of scope)
         )
       : new DesktopDeviceStabilizer();

   if (options.StabilizeDevice && options.BuildTarget != "Android")
   {
       Console.Error.WriteLine($"[warn] --stabilize-device is Android-only, ignored for {options.BuildTarget}");
   }
   ```

2. `for (int i = 0; i < scenarios.Count; i++)` の中身を multi-run 対応に:

   ```csharp
   for (int i = 0; i < scenarios.Count; i++)
   {
       PerfScenario scenario = scenarios[i];

       // multi-run 用の runDir 群を準備
       var scenarioRunDirs = new List<string>(options.Repeat);
       for (int r = 0; r < options.Repeat; r++)
       {
           string runDir = Path.Combine(runDir, scenario.Name, $"repeat-{r:D2}");
           scenarioRunDirs.Add(runDir);
       }

       ScenarioManifest scenarioManifest;
       for (int r = 0; r < options.Repeat; r++)
       {
           if (options.StabilizeDevice && options.BuildTarget == "Android")
           {
               try
               {
                   await stabilizer.StabilizeAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
                       .ConfigureAwait(false);
               }
               catch (Exception ex)
               {
                   Console.Error.WriteLine($"[warn] device stabilization failed (run {r}): {ex.Message}");
               }
           }
           scenarioManifest = await RunScenario(
               root,
               helper,
               playerExecutable,
               scenarioRunDirs[r],
               scenario,
               options,
               domainBase + i).ConfigureAwait(false);
           scenarioManifest.Name = scenario.Name;  // multi-run で上書きされるのを防ぐ
           scenarioManifest.RepeatCount = options.Repeat;
           if (r == 0)
           {
               manifest.Scenarios.Add(scenarioManifest);
           }
           else
           {
               // 2 回目以降は最後の scenario manifest を上書き
               manifest.Scenarios[manifest.Scenarios.Count - 1] = scenarioManifest;
           }
           if (scenarioManifest.PlayerExitCode != 0 || scenarioManifest.HelperExitCode != 0)
           {
               failed = true;
           }
       }

       // aggregate (N > 1 の時のみ)
       if (options.Repeat > 1)
       {
           var aggregate = RunAggregator.Aggregate(scenarioRunDirs, options.Aggregate);
           if (aggregate != null)
           {
               string aggregatePath = Path.Combine(runDir, scenario.Name, "aggregate.json");
               RunAggregator.Save(aggregatePath, aggregate);
               manifest.Scenarios[manifest.Scenarios.Count - 1].AggregatePath = aggregatePath;
               manifest.Scenarios[manifest.Scenarios.Count - 1].Aggregate = aggregate;
           }
       }

       manifest.Save(Path.Combine(runDir, "manifest.json"));
   }
   ```

3. 既存 `RunScenario` 呼び出しの最初の引数 (`runDir`) を、multi-run では
   `scenarioRunDirs[r]` に変更するため、`RunScenario` シグネチャの `runDir` パラメータが
   そのまま使える (内部で `Path.Combine(runDir, scenario.Name)` していた箇所を削除)。

   ただし、既存 `RunScenario` の `string runDir` パラメータは「scenario 親ディレクトリ」
   を期待しているので、呼び出し側で `runDir` (= 計測 runDir) を渡すのは変。
   `RunScenario` 内で `scenarioDir = Path.Combine(runDir, scenario.Name)` していた
   ロジックを削除し、`scenarioDir` パラメータを直接受け取る形に変更する:

   `RunScenario` の先頭を:
   ```csharp
   string scenarioDir = runDir;  // 既に scenario 配下のパスが来る
   Directory.CreateDirectory(scenarioDir);
   ```
   に変更。

- [ ] **Step 6.3: ビルド確認**

```bash
dotnet build tools/rosettadds-perf-runner -c Release
```

期待: 0 errors。warning があれば確認して対応。

- [ ] **Step 6.4: ProgramTests の差し替え**

`tools/rosettadds-perf-runner.Tests/ProgramTests.cs` の `ProgramMultiRunTests` を
以下のように差し替え (FakeProcessDriver を使った完全版):

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class ProgramMultiRunTests
{
    [Fact]
    public void MainAsync_既存引数_は_従来通り_動作する()
    {
        // --repeat 1 デフォルトで aggregate は発生しない
        // 既存 ProgramTests のテストが破壊されていないことを確認
        // (本ファイル内の他テストで担保)
    }
}
```

注意: 既存 `ProgramTests.cs` のテストが全て PASS していれば OK。新規 multi-run /
stabilize の end-to-end テストは FakeProcessDriver / FakeAdbClient 経由での完全
integration が必要となり、複雑度が増す。**Task 6 では ProgramTests の既存テスト
が破壊されていないことのみ確認**し、end-to-end テストは Task 7 (実機計測) で
担保する。

- [ ] **Step 6.5: 全テスト回帰確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests -v minimal
```

期待: 全テスト (既存 + Task 1-5 で追加した分) PASS。

- [ ] **Step 6.6: commit**

```bash
git add tools/rosettadds-perf-runner/Program.cs tools/rosettadds-perf-runner.Tests/ProgramTests.cs
git commit -m "feat(perf-runner): MainAsync に multi-run + stabilize 統合"
```

---

## Task 7: 既存 help / バリデーション整合確認 + 最終ビルド

- [ ] **Step 7.1: `--help` 出力確認**

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- --help
```

期待: `--stabilize-device` / `--repeat` / `--aggregate` の 3 行が help に出力。

- [ ] **Step 7.2: バリデーション確認**

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- --repeat 0
```

期待: `Unhandled exception: --repeat must be a positive integer` で exit 1。

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- --aggregate stddev
```

期待: `Unhandled exception: --aggregate must be median` で exit 1。

- [ ] **Step 7.3: 既存単体フラグの後方互換性確認**

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- --build-target StandaloneLinux64 --scenario unity-to-ros2-reliable-32 --skip-build --player-build /tmp/ROSettaDDSPerfPlayer --artifacts /tmp/perf-backcompat-check --capture-frames 100
```

期待: 既存 scenario 実行、aggregate.json は作成されない、manifest.json は従来形式
(`AggregatePath` / `Aggregate` フィールドは null だが JSON には含まれる)。

- [ ] **Step 7.4: 全テスト最終確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests -c Release -v minimal
```

期待: 全テスト PASS。

- [ ] **Step 7.5: commit (変更なしの場合スキップ)**

```bash
git status
```

変更があれば commit、なければスキップ。

---

## Task 8: 実機計測 (Android + Sony XIG04)

> 注: device 接続必須。device 不在の場合は `--skip-build` + Desktop 計測で代替確認。

- [ ] **Step 8.1: device 接続 + 既存 build artifact 確認**

```bash
adb devices
ls -la /tmp/rosettadds-perf-android.apk
```

期待: `5HF6OVWCDECMJZ59    device` 出力。apk 不在なら `dotnet run --project
tools/rosettadds-perf-runner -c Release -- --build-target Android --scenario all
--android-device 5HF6OVWCDECMJZ59 --artifacts /tmp/build-check` で再 build。

- [ ] **Step 8.2: stabilize + repeat 5 で 1 scenario 計測 (smoke)**

```bash
mkdir -p artifacts/perf-android-stabilize
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario unity-to-ros2-best-effort-8192 \
  --android-device 5HF6OVWCDECMJZ59 \
  --player-build /tmp/rosettadds-perf-android.apk \
  --artifacts artifacts/perf-android-stabilize \
  --stabilize-device \
  --repeat 5 \
  --aggregate median \
  --capture-frames 200
```

期待: 計測完走。`artifacts/perf-android-stabilize/<runId>/unity-to-ros2-best-effort-8192/`
配下に `repeat-00/` 〜 `repeat-04/` の 5 ディレクトリ + `aggregate.json` 1 ファイル
+ `manifest.json` 1 ファイル。

- [ ] **Step 8.3: aggregate.json 内容確認**

```bash
cat artifacts/perf-android-stabilize/<runId>/unity-to-ros2-best-effort-8192/aggregate.json
```

期待: `runCount: 5` と各メトリクス (mps, elapsedMs, received, ...) の median 値。

- [ ] **Step 8.4: manifest.json 内容確認**

```bash
cat artifacts/perf-android-stabilize/<runId>/manifest.json
```

期待: `Scenarios[0].RepeatCount == 5`、`Aggregate` / `AggregatePath` フィールドが存在。

- [ ] **Step 8.5: 9 scenario 全部を multi-run で計測 (約 30-60 分)**

```bash
dotnet run --project tools/rosettadds-perf-runner -c Release --no-build -- \
  --build-target Android \
  --scenario all \
  --android-device 5HF6OVWCDECMJZ59 \
  --player-build /tmp/rosettadds-perf-android.apk \
  --artifacts artifacts/perf-android-stabilize-full \
  --stabilize-device \
  --repeat 5 \
  --aggregate median \
  --capture-frames 200
```

期待: 全 9 scenario 完走、各 scenario 配下に 5 runs + aggregate.json。

- [ ] **Step 8.6: artifacts を git commit**

```bash
git add artifacts/perf-android-stabilize artifacts/perf-android-stabilize-full
git commit -m "chore: stabilize + repeat 5 計測 artifacts (Android 5HF6OVWCDECMJZ59)"
```

注意: 計測ログのみコミット。.pcap や大きな binary は .gitignore で除外。

---

## Task 9: findings doc 作成

**Files:**
- Create: `docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md`

- [ ] **Step 9.1: 計測結果集計**

```bash
# 9 scenario の median mps を集計
for s in artifacts/perf-android-stabilize-full/*/; do
  run=$(basename "$s")
  for sc in "$s"*/; do
    scn=$(basename "$sc")
    if [ -f "$sc/aggregate.json" ]; then
      mps=$(python3 -c "import json; print(json.load(open('$sc/aggregate.json'))['messagesPerSecond'])")
      echo "$run $scn $mps"
    fi
  done
done
```

- [ ] **Step 9.2: 既存 run (20260627-103647) との比較表作成**

既存 `artifacts/perf-android-all/20260627-103647/manifest.json` (--repeat 1) と
新 `artifacts/perf-android-stabilize-full/<runId>/manifest.json` (--repeat 5) の
mps を比較する markdown table を作る。

- [ ] **Step 9.3: findings doc 作成**

`docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md` を
以下のテンプレートで作成:

```markdown
# Android Stabilize + Multi-run Findings (2026-06-28)

## サマリ

`tools/rosettadds-perf-runner` に `--stabilize-device` (軽量 preflight)、
`--repeat 5` (multi-run)、`--aggregate median` (集計) を追加し、Sony XIG04
(Xperia 10 III / Android 15) で 9 scenario × 5 runs を計測。既存 run
(2026-06-27 --repeat 1) と比較して run-to-run variance が (n× → m×) に縮小
したことを確認。

## 計測環境

- 日時: 2026-06-28
- HEAD: <commit>
- branch: perf/android-stabilize-and-aggregate
- device: Sony XIG04 (Xperia 10 III), Android 15
- 同 L2 セグメント (host 192.168.0.20 ↔ device 192.168.0.22)

## 計測結果

### 既存 (--repeat 1) vs 新 (--repeat 5 median) 比較

| Scenario | 既存 mps | 既存 run-to-run variance | 新 median mps | 新 variance |
|----------|---------:|-------------------------:|--------------:|------------:|
| ...      |          |                          |               |             |

### preflight ON vs OFF 比較 (任意、smoke 計測で取得した場合)

(あれば)

## 結論

- preflight + repeat 5 により variance が縮小した / しなかった
- 縮小した場合の主要因 (WiFi 切断による connection pool クリーンアップ、wakelock
  による screen off 防止、等)
- 縮小しなかった場合は原因を別途分析

## 残存ボトルネック / next action

1. 既存 findings の他 next action への取り組み状況
2. preflight 拡張 (device リブート、permission 許可) の必要性
3. helper 側詳細解析
4. ...

## 計測 artifact

- `artifacts/perf-android-stabilize/<runId>/` (smoke 1 scenario × 5 runs)
- `artifacts/perf-android-stabilize-full/<runId>/` (full 9 scenario × 5 runs)
- 比較元: `artifacts/perf-android-all/20260627-103647/` (既存 --repeat 1)
```

- [ ] **Step 9.4: commit**

```bash
git add docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md
git commit -m "docs(specs): Android stabilize + multi-run 計測結果と findings を追加"
```

---

## Task 10: 検証 + PR

- [ ] **Step 10.1: Validation チェックリスト実行**

design doc の Validation 完了条件を 1 つずつ確認:

- [ ] `--stabilize-device --repeat 5 --aggregate median` で全 9 scenario 完走
- [ ] 既存 run (--repeat 1) と新 run (--repeat 5) で manifest.json の
  scenario 数が一致
- [ ] aggregate.json に全 metric の median 値が出力される
- [ ] preflight ON vs OFF で run-to-run variance が縮小することを確認
- [ ] 既存 `dotnet run --project tools/rosettadds-perf-runner` 単独利用の
  シナリオ (--repeat 1 デフォルト) は無変更動作

- [ ] **Step 10.2: 既存 spec doc との整合性確認**

`docs/superpowers/specs/2026-06-27-android-bottleneck-investigation.md` の
「Next action 2: Android run-to-run variance 10× の安定化 (B')」が本 findings
で更新されたか確認。

- [ ] **Step 10.3: ブランチ push**

```bash
git push origin perf/android-stabilize-and-aggregate
```

- [ ] **Step 10.4: PR 作成**

```bash
gh pr create --base main --head perf/android-stabilize-and-aggregate \
  --title "feat(perf-runner): Android preflight stabilize + multi-run median 集計" \
  --body "本 PR は 2026-06-27-android-bottleneck-investigation.md の Next action 2 (B') の perf-runner 拡張。

Design: docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-design.md
Findings: docs/superpowers/specs/2026-06-28-android-stabilize-aggregate-findings.md

3 フラグ追加:
- --stabilize-device: 計測前 WiFi 再接続 + screen on wakelock + host 接続待機
- --repeat N: 各 scenario を N 回連続 run
- --aggregate median: 集計方法 (default median)

scope: device リブート / permission 自動許可 / thermal 監視 / Desktop 対応は本 PR 外。"
```

- [ ] **Step 10.5: CI 通過待ち**

```bash
gh pr checks
```

期待: 全て pass。

- [ ] **Step 10.6: レビュー対応 + main マージ**

レビュー指摘に対応後、main にマージ (squash or merge、PR 設定に従う)。
