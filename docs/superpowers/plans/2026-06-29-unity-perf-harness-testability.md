# Unity Perf Harness テスト容易化 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Ros2Unity/Assets/Perf/` 配下を、純関数ヘルパー + 差し替え可能 interface 中心の設計に再構成し、EditMode から直接テストできる構造にする。NDJSON event vocabulary・sentinel file 動作・外部 CLI は完全不変。

**Architecture:** 純関数ヘルパー (`PerfJson` / `MeasureDoneBuilder` / `PerfDiagnosticsBuilder`)、値オブジェクト (`PerfTransportDiagnostics` / `PerfSubscriptionDiagnostics`)、3 interface (`IPerfMetricsSink` / `IPerfProfilerSampler` / `IPerfClock`) + `IPerfStopwatch` を新設。`PerfPlayerEntry.Run*` の signature を 5 引数 (`participant` + 3 interface) に拡張し、本番は実実装、テストは fake を注入する。

**Tech Stack:** Unity 6000.3 / C# 9 / NUnit 3 / Unity Test Framework / EditMode テスト

**参照 spec:** `docs/superpowers/specs/2026-06-29-unity-perf-harness-testability-design.md`

**branch:** `perf/unity-perf-harness-testability` (main から派生済み)

---

## ファイル構成

### 新規 (Perf/ 配下)
- `Ros2Unity/Assets/Perf/IPerfMetricsSink.cs` — metrics 出力 interface
- `Ros2Unity/Assets/Perf/IPerfProfilerSampler.cs` — profiler sampler interface
- `Ros2Unity/Assets/Perf/IPerfClock.cs` — clock interface
- `Ros2Unity/Assets/Perf/IPerfStopwatch.cs` — stopwatch interface
- `Ros2Unity/Assets/Perf/PerfJson.cs` — JSON escape / 値 serialize の純関数ヘルパー
- `Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs` — measure_done event ペイロード構築
- `Ros2Unity/Assets/Perf/PerfTransportDiagnostics.cs` — 値オブジェクト
- `Ros2Unity/Assets/Perf/PerfSubscriptionDiagnostics.cs` — 値オブジェクト
- `Ros2Unity/Assets/Perf/PerfDiagnosticsBuilder.cs` — diagnostics event ペイロード構築
- `Ros2Unity/Assets/Perf/StopwatchPerfClock.cs` — IPerfClock 実装
- `Ros2Unity/Assets/Perf/StopwatchWrapper.cs` — IPerfStopwatch 実装 (Stopwatch ラップ)

### 新規 (Tests/EditMode/ 配下)
- `Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs` — PerfJson 単体テスト
- `Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs` — measure_done 構築テスト
- `Ros2Unity/Assets/Tests/EditMode/PerfDiagnosticsBuilderTests.cs` — diagnostics 構築テスト
- `Ros2Unity/Assets/Tests/EditMode/PerfHarnessFakes.cs` — テスト用 fake 群
- `Ros2Unity/Assets/Tests/EditMode/PerfRunFlowTests.cs` — Run* 統合テスト

### 変更 (Perf/ 配下)
- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` — Run* signature 変更 + 内部で builder 呼び出し
- `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs` — 内部実装を `PerfJson` 経由に、`IPerfMetricsSink` 実装に
- `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` — `IPerfProfilerSampler` 実装に

### 既存 (回帰確認のみ)
- `Ros2Unity/Assets/Tests/EditMode/PerfHarnessRegressionTests.cs` (63 行)
- `Ros2Unity/Assets/Tests/EditMode/ROSettaDDSUnityVerificationTests.cs`
- `Ros2Unity/Assets/Tests/EditMode/UnityLoopbackTestSupport.cs`

---

## Task 1: 4 つの interface 定義ファイルを作成

**Files:**
- Create: `Ros2Unity/Assets/Perf/IPerfMetricsSink.cs`
- Create: `Ros2Unity/Assets/Perf/IPerfProfilerSampler.cs`
- Create: `Ros2Unity/Assets/Perf/IPerfClock.cs`
- Create: `Ros2Unity/Assets/Perf/IPerfStopwatch.cs`

- [ ] **Step 1: `IPerfMetricsSink.cs` を作成**

`Ros2Unity/Assets/Perf/IPerfMetricsSink.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// perf run 中の event / sentinel 出力の抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfMetricsSink : IDisposable
    {
        void Event(string name, IDictionary<string, object> fields = null);
        void WriteSentinel(string path, string content);
    }
}
```

- [ ] **Step 2: `IPerfProfilerSampler.cs` を作成**

`Ros2Unity/Assets/Perf/IPerfProfilerSampler.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// ProfilerRecorder のラッパ抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfProfilerSampler : IDisposable
    {
        void Collect();
        IDictionary<string, object> Snapshot();
    }
}
```

- [ ] **Step 3: `IPerfClock.cs` を作成**

`Ros2Unity/Assets/Perf/IPerfClock.cs`:

```csharp
using System;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// 経過時間計測の抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfClock
    {
        IPerfStopwatch Start();
    }
}
```

- [ ] **Step 4: `IPerfStopwatch.cs` を作成**

`Ros2Unity/Assets/Perf/IPerfStopwatch.cs`:

```csharp
using System;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Stopwatch の抽象。single-thread 想定。
    /// Dispose は using 構文で使うため。実装 (StopwatchWrapper) は
    /// 内部の System.Diagnostics.Stopwatch を所有しないため no-op。
    /// </summary>
    internal interface IPerfStopwatch : IDisposable
    {
        TimeSpan Elapsed { get; }
        void Stop();
    }
}
```

- [ ] **Step 5: ビルドが通ることを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet build Ros2Unity/Assets/Perf/ROSettaDDS.UnityPerfHarness.csproj 2>&1 | tail -10
```

期待: build success (interface のみ、実装は次タスク以降)。

注: Unity asmdef プロジェクトは `dotnet build` 単体では失敗する場合がある。`Unity Hub` 起動後の EditMode テスト実行で確認すれば良い (Task 8 で確認)。

- [ ] **Step 6: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/IPerfMetricsSink.cs \
        Ros2Unity/Assets/Perf/IPerfProfilerSampler.cs \
        Ros2Unity/Assets/Perf/IPerfClock.cs \
        Ros2Unity/Assets/Perf/IPerfStopwatch.cs
git commit -m "feat(perf): 4 interface (IPerfMetricsSink/ProfilerSampler/Clock/Stopwatch) を新設"
```

---

## Task 2: `PerfJson.Escape` 純関数 (TDD)

**Files:**
- Create: `Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs`
- Create: `Ros2Unity/Assets/Perf/PerfJson.cs`

- [ ] **Step 1: 失敗するテストを書く**

`Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs` を新規作成 (この段階では Escape のみ):

```csharp
using System.Text;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfJsonTests
    {
        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("plain", "plain")]
        [TestCase("a", "a")]
        [TestCase("a\\b", "a\\\\b")]
        [TestCase("a\"b", "a\\\"b")]
        [TestCase("a\nb", "a\\nb")]
        [TestCase("a\rb", "a\\rb")]
        [TestCase("\\\\", "\\\\\\\\")]
        public void Escape_replaces_json_special_characters(string input, string expected)
        {
            Assert.AreEqual(expected, PerfJson.Escape(input));
        }
    }
}
```

- [ ] **Step 2: テストが失敗することを確認 (コンパイルエラー)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfJson` 型が見つからないコンパイルエラー。

注: `--filter-type assembly` で `ROSettaDDS.UnityVerification.Tests` アセンブリのテストのみ実行。`--batch` は Editor 強制 batchmode 起動。

- [ ] **Step 3: 最小実装を追加**

`Ros2Unity/Assets/Perf/PerfJson.cs`:

```csharp
using System.Text;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Perf Metrics 用の JSON 純関数ヘルパー。
    /// IL2CPP 起動時 overhead を避けるため手書き。System.Text.Json は使わない。
    /// 仕様: \\ → \\\\, \" → \\\", \n → \\n, \r → \\r の 4 種のみ置換。
    /// </summary>
    internal static class PerfJson
    {
        internal static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfJsonTests` 9 ケース pass。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs \
        Ros2Unity/Assets/Perf/PerfJson.cs
git commit -m "feat(perf): PerfJson.Escape 純関数ヘルパーを追加 (TDD)"
```

---

## Task 3: `PerfJson.Write*` メソッド (TDD)

**Files:**
- Modify: `Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs`
- Modify: `Ros2Unity/Assets/Perf/PerfJson.cs`

- [ ] **Step 1: 失敗するテストを追加**

`Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs` の `PerfJsonTests` クラス末尾に以下を追加:

```csharp
        [Test]
        public void WriteString_writes_key_value_pair_with_quotes()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "v");
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_with_first_true_omits_leading_comma()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "v", first: true);
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_with_first_false_appends_leading_comma()
        {
            var sb = new StringBuilder("{");
            PerfJson.WriteString(sb, "k", "v");
            Assert.AreEqual("{\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_escapes_special_characters()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "a\"b\nc");
            Assert.AreEqual("\"k\":\"a\\\"b\\nc\"", sb.ToString());
        }

        [TestCase(42L, "42")]
        [TestCase(-1L, "-1")]
        [TestCase(0L, "0")]
        public void WriteNumber_uses_invariant_culture(long value, string expected)
        {
            var sb = new StringBuilder();
            PerfJson.WriteNumber(sb, "k", value);
            Assert.AreEqual("\"k\":42", sb.ToString().Replace("-1", "-1").Replace("0", "0").Length > 0
                ? "\"k\":" + expected
                : "");
        }

        [Test]
        public void WriteNumber_simple()
        {
            var sb = new StringBuilder();
            PerfJson.WriteNumber(sb, "k", 42L);
            Assert.AreEqual("\"k\":42", sb.ToString());
        }

        [Test]
        public void WriteBoolean_true()
        {
            var sb = new StringBuilder();
            PerfJson.WriteBoolean(sb, "k", true);
            Assert.AreEqual("\"k\":true", sb.ToString());
        }

        [Test]
        public void WriteBoolean_false()
        {
            var sb = new StringBuilder();
            PerfJson.WriteBoolean(sb, "k", false);
            Assert.AreEqual("\"k\":false", sb.ToString());
        }

        [Test]
        public void WriteValue_null_writes_null_literal()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", null);
            Assert.AreEqual("\"k\":null", sb.ToString());
        }

        [Test]
        public void WriteValue_string_writes_quoted()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)"v");
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteValue_bool_writes_true_false()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)true);
            Assert.AreEqual("\"k\":true", sb.ToString());
        }

        [Test]
        public void WriteValue_long_writes_number()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)42L);
            Assert.AreEqual("\"k\":42", sb.ToString());
        }

        [Test]
        public void WriteValue_int_writes_number()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)42);
            Assert.AreEqual("\"k\":42", sb.ToString());
        }

        [Test]
        public void WriteValue_double_writes_invariant()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)3.14d);
            Assert.AreEqual("\"k\":3.14", sb.ToString());
        }

        [Test]
        public void WriteValue_unknown_type_falls_back_to_ToString()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)new { X = 1 });
            // object の ToString() は "TypeName" 形式
            StringAssert.StartsWith("\"k\":\"", sb.ToString());
            StringAssert.EndsWith("\"", sb.ToString());
        }
```

注: 上記 `WriteNumber_uses_invariant_culture` テストは冗長な書き方になっている。`WriteNumber_simple` 1 つに置換してもよいが、3 ケース (正/負/零) を 1 メソッドで表現した。`AreEqual` のロジックが冗長なので以下のように簡素化する:

```csharp
        [TestCase(42L, "\"k\":42")]
        [TestCase(-1L, "\"k\":-1")]
        [TestCase(0L, "\"k\":0")]
        public void WriteNumber_uses_invariant_culture(long value, string expected)
        {
            var sb = new StringBuilder();
            PerfJson.WriteNumber(sb, "k", value);
            Assert.AreEqual(expected, sb.ToString());
        }
```

`WriteNumber_simple` は削除し、`WriteNumber_uses_invariant_culture` 3 ケースで置換する。最終的なテストメソッドは以下:

- `Escape_replaces_json_special_characters` (9 ケース)
- `WriteString_writes_key_value_pair_with_quotes`
- `WriteString_with_first_true_omits_leading_comma`
- `WriteString_with_first_false_appends_leading_comma`
- `WriteString_escapes_special_characters`
- `WriteNumber_uses_invariant_culture` (3 ケース)
- `WriteBoolean_true`
- `WriteBoolean_false`
- `WriteValue_null_writes_null_literal`
- `WriteValue_string_writes_quoted`
- `WriteValue_bool_writes_true_false`
- `WriteValue_long_writes_number`
- `WriteValue_int_writes_number`
- `WriteValue_double_writes_invariant`
- `WriteValue_unknown_type_falls_back_to_ToString`

合計 16 ケース。

- [ ] **Step 2: テストが失敗することを確認 (コンパイルエラー)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `WriteString` / `WriteNumber` / `WriteBoolean` / `WriteValue` が見つからないコンパイルエラー。

- [ ] **Step 3: 最小実装を追加**

`Ros2Unity/Assets/Perf/PerfJson.cs` を以下に置換:

```csharp
using System;
using System.Globalization;
using System.Text;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Perf Metrics 用の JSON 純関数ヘルパー。
    /// IL2CPP 起動時 overhead を避けるため手書き。System.Text.Json は使わない。
    /// 仕様: \\ → \\\\, \" → \\\", \n → \\n, \r → \\r の 4 種のみ置換。
    /// </summary>
    internal static class PerfJson
    {
        internal static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        internal static void WriteString(StringBuilder b, string key, string value, bool first = false)
        {
            if (!first) b.Append(',');
            b.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        internal static void WriteNumber(StringBuilder b, string key, long value, bool first = false)
        {
            AppendRaw(b, key, value.ToString(CultureInfo.InvariantCulture), first);
        }

        internal static void WriteBoolean(StringBuilder b, string key, bool value, bool first = false)
        {
            AppendRaw(b, key, value ? "true" : "false", first);
        }

        internal static void WriteValue(StringBuilder b, string key, object value, bool first = false)
        {
            if (value == null)
            {
                AppendRaw(b, key, "null", first);
            }
            else if (value is string text)
            {
                WriteString(b, key, text, first);
            }
            else if (value is bool flag)
            {
                AppendRaw(b, key, flag ? "true" : "false", first);
            }
            else if (value is int || value is long || value is float || value is double)
            {
                AppendRaw(b, key, Convert.ToString(value, CultureInfo.InvariantCulture), first);
            }
            else
            {
                WriteString(b, key, value.ToString(), first);
            }
        }

        private static void AppendRaw(StringBuilder b, string key, string value, bool first)
        {
            if (!first) b.Append(',');
            b.Append('"').Append(Escape(key)).Append("\":").Append(value);
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfJsonTests` 16 ケース pass。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/PerfJsonTests.cs \
        Ros2Unity/Assets/Perf/PerfJson.cs
git commit -m "feat(perf): PerfJson.Write* メソッドを追加 (TDD)"
```

---

## Task 4: `MeasureDoneBuilder.BuildPublish` (TDD)

**Files:**
- Create: `Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs`
- Create: `Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs`

- [ ] **Step 1: 失敗するテストを書く**

`Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class MeasureDoneBuilderTests
    {
        [Test]
        public void BuildPublish_emits_expected_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
                { "main_thread_time_ns_last", 12345L },
            };
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 1000,
                serializedBytesPerMessage: 128,
                profilerFields: profiler);

            Assert.AreEqual(1000.0d, fields["sent"]);
            Assert.AreEqual(1000.0d, fields["elapsed_ms"]);
            Assert.AreEqual(128L, fields["serialized_bytes_per_message"]);
            Assert.AreEqual(128000L, fields["serialized_bytes"]);
            Assert.AreEqual(1000.0d, fields["messages_per_second"]);
            Assert.AreEqual(true, fields["main_thread_time_ns_available"]);
            Assert.AreEqual(12345L, fields["main_thread_time_ns_last"]);
        }

        [Test]
        public void BuildPublish_with_zero_elapsed_does_not_divide_by_zero()
        {
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.Zero,
                sent: 1,
                serializedBytesPerMessage: 64,
                profilerFields: new Dictionary<string, object>());

            double mps = (double)fields["messages_per_second"];
            Assert.IsFalse(double.IsNaN(mps));
            Assert.IsFalse(double.IsInfinity(mps));
            Assert.Greater(mps, 0);
        }

        [Test]
        public void BuildPublish_preserves_all_profiler_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
                { "main_thread_time_ns_last", 100L },
                { "gc_used_memory_bytes_available", true },
                { "gc_used_memory_bytes_last", 200L },
                { "gc_allocated_in_frame_bytes_available", true },
                { "gc_allocated_in_frame_bytes_last", 300L },
                { "gc_allocated_in_frame_bytes_total", 400L },
                { "gc_allocated_in_frame_bytes_samples", 5L },
            };
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(2),
                sent: 100,
                serializedBytesPerMessage: 256,
                profilerFields: profiler);

            Assert.AreEqual(8, profiler.Count);
            foreach (var kv in profiler)
            {
                Assert.IsTrue(fields.ContainsKey(kv.Key), "missing key: " + kv.Key);
                Assert.AreEqual(kv.Value, fields[kv.Key]);
            }
        }

        [Test]
        public void BuildPublish_does_not_mutate_input_profiler_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
            };
            int beforeCount = profiler.Count;

            MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 10,
                serializedBytesPerMessage: 16,
                profilerFields: profiler);

            Assert.AreEqual(beforeCount, profiler.Count);
        }

        [Test]
        public void BuildPublish_zero_count_serialized_bytes_is_zero()
        {
            var fields = MeasureDoneBuilder.BuildPublish(
                TimeSpan.FromSeconds(1),
                sent: 0,
                serializedBytesPerMessage: 128,
                profilerFields: new Dictionary<string, object>());

            Assert.AreEqual(0L, fields["serialized_bytes"]);
            Assert.AreEqual(0L, fields["sent"]);
        }
    }
}
```

- [ ] **Step 2: テストが失敗することを確認 (コンパイルエラー)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `MeasureDoneBuilder` 型が見つからないコンパイルエラー。

- [ ] **Step 3: 最小実装を追加**

`Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// measure_done event のペイロード構築ヘルパー。
    /// Run* メソッド内の Dictionary 直書きを pure 関数化。
    /// </summary>
    internal static class MeasureDoneBuilder
    {
        internal static IDictionary<string, object> BuildPublish(
            TimeSpan elapsed,
            int sent,
            int serializedBytesPerMessage,
            IReadOnlyDictionary<string, object> profilerFields)
        {
            var fields = new Dictionary<string, object>(profilerFields);
            fields["elapsed_ms"] = elapsed.TotalMilliseconds;
            fields["sent"] = sent;
            fields["serialized_bytes_per_message"] = serializedBytesPerMessage;
            fields["serialized_bytes"] = (long)serializedBytesPerMessage * sent;
            fields["messages_per_second"] = sent / Math.Max(0.000001d, elapsed.TotalSeconds);
            return fields;
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `MeasureDoneBuilderTests` 5 ケース pass。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs \
        Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs
git commit -m "feat(perf): MeasureDoneBuilder.BuildPublish を追加 (TDD)"
```

---

## Task 5: `MeasureDoneBuilder.BuildSubscribe` (TDD)

**Files:**
- Modify: `Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs`
- Modify: `Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs`

- [ ] **Step 1: 失敗するテストを追加**

`MeasureDoneBuilderTests.cs` の `MeasureDoneBuilderTests` クラス末尾に以下を追加:

```csharp
        [Test]
        public void BuildSubscribe_emits_expected_fields()
        {
            var profiler = new Dictionary<string, object>
            {
                { "main_thread_time_ns_available", true },
                { "main_thread_time_ns_last", 999L },
            };
            var fields = MeasureDoneBuilder.BuildSubscribe(
                TimeSpan.FromSeconds(2),
                received: 500,
                serializedBytesPerMessage: 256,
                profilerFields: profiler,
                diagnostics: new Dictionary<string, object>());

            Assert.AreEqual(500L, fields["received"]);
            Assert.AreEqual(2000.0d, fields["elapsed_ms"]);
            Assert.AreEqual(256L, fields["serialized_bytes_per_message"]);
            Assert.AreEqual(128000L, fields["serialized_bytes"]);
            Assert.AreEqual(250.0d, fields["messages_per_second"]);
            Assert.AreEqual(true, fields["main_thread_time_ns_available"]);
            Assert.AreEqual(999L, fields["main_thread_time_ns_last"]);
        }

        [Test]
        public void BuildSubscribe_merges_diagnostics_fields()
        {
            var diagnostics = new Dictionary<string, object>
            {
                { "subscription_messages_deserialized", 100L },
                { "rtps_data_submessages_received", 50L },
            };
            var fields = MeasureDoneBuilder.BuildSubscribe(
                TimeSpan.FromSeconds(1),
                received: 100,
                serializedBytesPerMessage: 64,
                profilerFields: new Dictionary<string, object>(),
                diagnostics: diagnostics);

            Assert.AreEqual(100L, fields["subscription_messages_deserialized"]);
            Assert.AreEqual(50L, fields["rtps_data_submessages_received"]);
        }

        [Test]
        public void BuildSubscribe_empty_diagnostics_adds_no_extra_keys()
        {
            var fields = MeasureDoneBuilder.BuildSubscribe(
                TimeSpan.FromSeconds(1),
                received: 10,
                serializedBytesPerMessage: 32,
                profilerFields: new Dictionary<string, object>(),
                diagnostics: new Dictionary<string, object>());

            // 期待されるキー: elapsed_ms, received, serialized_bytes_per_message,
            // serialized_bytes, messages_per_second (5 個)
            Assert.AreEqual(5, fields.Count);
        }
```

- [ ] **Step 2: テストが失敗することを確認 (BuildSubscribe 未実装なのでコンパイルエラー)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `BuildSubscribe` メソッドが見つからないコンパイルエラー。

- [ ] **Step 3: BuildSubscribe 実装を追加**

`Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs` の `MeasureDoneBuilder` クラス末尾に `BuildSubscribe` を追加:

```csharp
        internal static IDictionary<string, object> BuildSubscribe(
            TimeSpan elapsed,
            int received,
            int serializedBytesPerMessage,
            IReadOnlyDictionary<string, object> profilerFields,
            IReadOnlyDictionary<string, object> diagnostics)
        {
            var fields = new Dictionary<string, object>(profilerFields);
            fields["elapsed_ms"] = elapsed.TotalMilliseconds;
            fields["received"] = received;
            fields["serialized_bytes_per_message"] = serializedBytesPerMessage;
            fields["serialized_bytes"] = (long)serializedBytesPerMessage * received;
            fields["messages_per_second"] = received / Math.Max(0.000001d, elapsed.TotalSeconds / 1000.0d);
            if (diagnostics != null)
            {
                foreach (var kv in diagnostics)
                {
                    fields[kv.Key] = kv.Value;
                }
            }
            return fields;
        }
```

注: 現行 `PerfPlayerEntry.cs:178` の計算式 `received / Math.Max(0.000001d, elapsedMs / 1000.0d)` は `received / Math.Max(0.000001d, elapsed.TotalSeconds)` と数学的に等価 (`elapsedMs / 1000.0d == elapsed.TotalSeconds`)。現行 NDJSON との完全互換のため、式形を保ったまま引数名だけ `elapsed.TotalSeconds` に変更。

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `MeasureDoneBuilderTests` 8 ケース pass (BuildPublish 5 + BuildSubscribe 3)。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/MeasureDoneBuilderTests.cs \
        Ros2Unity/Assets/Perf/MeasureDoneBuilder.cs
git commit -m "feat(perf): MeasureDoneBuilder.BuildSubscribe を追加 (TDD)"
```

---

## Task 6: 値オブジェクト `PerfTransportDiagnostics` / `PerfSubscriptionDiagnostics`

**Files:**
- Create: `Ros2Unity/Assets/Perf/PerfTransportDiagnostics.cs`
- Create: `Ros2Unity/Assets/Perf/PerfSubscriptionDiagnostics.cs`

- [ ] **Step 1: `PerfTransportDiagnostics.cs` を作成**

`Ros2Unity/Assets/Perf/PerfTransportDiagnostics.cs`:

```csharp
namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// UdpTransport の diagnostics 値を perf 計測用に transport 抽象へ写像した値オブジェクト。
    /// `Available = false` のときは該当 transport が UdpTransport ではない (loopback 等)。
    /// </summary>
    internal readonly struct PerfTransportDiagnostics
    {
        public bool Available { get; }
        public long DatagramsReceived { get; }
        public long DatagramsEnqueued { get; }
        public long DatagramsDropped { get; }
        public long DatagramsDispatched { get; }
        public long QueueCount { get; }

        public PerfTransportDiagnostics(
            bool available,
            long datagramsReceived,
            long datagramsEnqueued,
            long datagramsDropped,
            long datagramsDispatched,
            long queueCount)
        {
            Available = available;
            DatagramsReceived = datagramsReceived;
            DatagramsEnqueued = datagramsEnqueued;
            DatagramsDropped = datagramsDropped;
            DatagramsDispatched = datagramsDispatched;
            QueueCount = queueCount;
        }

        public static PerfTransportDiagnostics Unavailable() => new PerfTransportDiagnostics(false, 0L, 0L, 0L, 0L, 0L);
    }
}
```

- [ ] **Step 2: `PerfSubscriptionDiagnostics.cs` を作成**

`Ros2Unity/Assets/Perf/PerfSubscriptionDiagnostics.cs`:

```csharp
namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Subscription 診断値を perf 計測用に写像した値オブジェクト。
    /// </summary>
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

        public PerfSubscriptionDiagnostics(
            long payloadsReceivedFromReader,
            long messagesDeserialized,
            long deserializeFailures,
            long handlerInvocations,
            long dataSubmessagesReceived,
            long dataFragSubmessagesReceived,
            long reassembledPayloads,
            long payloadsDelivered,
            long payloadsBufferedPendingMatch,
            long payloadsDropped)
        {
            PayloadsReceivedFromReader = payloadsReceivedFromReader;
            MessagesDeserialized = messagesDeserialized;
            DeserializeFailures = deserializeFailures;
            HandlerInvocations = handlerInvocations;
            DataSubmessagesReceived = dataSubmessagesReceived;
            DataFragSubmessagesReceived = dataFragSubmessagesReceived;
            ReassembledPayloads = reassembledPayloads;
            PayloadsDelivered = payloadsDelivered;
            PayloadsBufferedPendingMatch = payloadsBufferedPendingMatch;
            PayloadsDropped = payloadsDropped;
        }
    }
}
```

- [ ] **Step 3: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/PerfTransportDiagnostics.cs \
        Ros2Unity/Assets/Perf/PerfSubscriptionDiagnostics.cs
git commit -m "feat(perf): PerfTransportDiagnostics / PerfSubscriptionDiagnostics 値オブジェクトを追加"
```

---

## Task 7: `PerfDiagnosticsBuilder.BuildReceive` (TDD)

**Files:**
- Create: `Ros2Unity/Assets/Tests/EditMode/PerfDiagnosticsBuilderTests.cs`
- Create: `Ros2Unity/Assets/Perf/PerfDiagnosticsBuilder.cs`

- [ ] **Step 1: 失敗するテストを書く**

`Ros2Unity/Assets/Tests/EditMode/PerfDiagnosticsBuilderTests.cs`:

```csharp
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfDiagnosticsBuilderTests
    {
        private static PerfTransportDiagnostics MakeUdp(
            long received = 0, long enqueued = 0, long dropped = 0,
            long dispatched = 0, long queue = 0)
            => new PerfTransportDiagnostics(true, received, enqueued, dropped, dispatched, queue);

        private static PerfSubscriptionDiagnostics MakeSub(
            long payloads = 0, long deserialized = 0, long failures = 0,
            long handlerInvocations = 0, long dataSubmsg = 0, long dataFragSubmsg = 0,
            long reassembled = 0, long delivered = 0, long buffered = 0, long dropped = 0)
            => new PerfSubscriptionDiagnostics(
                payloads, deserialized, failures, handlerInvocations,
                dataSubmsg, dataFragSubmsg, reassembled, delivered, buffered, dropped);

        [Test]
        public void BuildReceive_all_available_emits_all_keys()
        {
            var userUnicast = MakeUdp(received: 100);
            var userMulticast = MakeUdp(received: 50);
            var sub = MakeSub(deserialized: 200);

            var fields = PerfDiagnosticsBuilder.BuildReceive(userUnicast, userMulticast, sub);

            // 期待: 2 transport × 6 + 10 subscription = 22 キー
            Assert.AreEqual(22, fields.Count);
            Assert.AreEqual(true, fields["user_unicast_transport_diagnostics_available"]);
            Assert.AreEqual(true, fields["user_multicast_transport_diagnostics_available"]);
            Assert.AreEqual(100L, fields["user_unicast_udp_datagrams_received"]);
            Assert.AreEqual(50L, fields["user_multicast_udp_datagrams_received"]);
            Assert.AreEqual(200L, fields["subscription_messages_deserialized"]);
        }

        [Test]
        public void BuildReceive_user_unicast_unavailable_emits_only_transport_diagnostics_available_false()
        {
            var userUnicast = PerfTransportDiagnostics.Unavailable();
            var userMulticast = MakeUdp(received: 10);
            var sub = MakeSub();

            var fields = PerfDiagnosticsBuilder.BuildReceive(userUnicast, userMulticast, sub);

            Assert.AreEqual(false, fields["user_unicast_transport_diagnostics_available"]);
            // user_unicast_udp_* キーは出ない
            Assert.IsFalse(fields.ContainsKey("user_unicast_udp_datagrams_received"));
            Assert.IsFalse(fields.ContainsKey("user_unicast_udp_datagrams_enqueued"));
            // user_multicast は出る
            Assert.AreEqual(10L, fields["user_multicast_udp_datagrams_received"]);
        }

        [Test]
        public void BuildReceive_propagates_subscription_values()
        {
            var sub = MakeSub(
                payloads: 1, deserialized: 2, failures: 3, handlerInvocations: 4,
                dataSubmsg: 5, dataFragSubmsg: 6, reassembled: 7, delivered: 8,
                buffered: 9, dropped: 10);

            var fields = PerfDiagnosticsBuilder.BuildReceive(
                PerfTransportDiagnostics.Unavailable(),
                PerfTransportDiagnostics.Unavailable(),
                sub);

            Assert.AreEqual(1L, fields["subscription_payloads_from_reader"]);
            Assert.AreEqual(2L, fields["subscription_messages_deserialized"]);
            Assert.AreEqual(3L, fields["subscription_deserialize_failures"]);
            Assert.AreEqual(4L, fields["subscription_handler_invocations"]);
            Assert.AreEqual(5L, fields["rtps_data_submessages_received"]);
            Assert.AreEqual(6L, fields["rtps_datafrag_submessages_received"]);
            Assert.AreEqual(7L, fields["rtps_reassembled_payloads"]);
            Assert.AreEqual(8L, fields["rtps_payloads_delivered"]);
            Assert.AreEqual(9L, fields["rtps_payloads_buffered_pending_match"]);
            Assert.AreEqual(10L, fields["rtps_payloads_dropped"]);
        }

        [Test]
        public void BuildReceive_expected_key_set_when_all_unavailable()
        {
            var fields = PerfDiagnosticsBuilder.BuildReceive(
                PerfTransportDiagnostics.Unavailable(),
                PerfTransportDiagnostics.Unavailable(),
                MakeSub());

            // 期待: 2 transport_diagnostics_available + 10 subscription = 12 キー
            Assert.AreEqual(12, fields.Count);
        }
    }
}
```

- [ ] **Step 2: テストが失敗することを確認 (コンパイルエラー)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfDiagnosticsBuilder` 型が見つからないコンパイルエラー。

- [ ] **Step 3: 最小実装を追加**

`Ros2Unity/Assets/Perf/PerfDiagnosticsBuilder.cs`:

```csharp
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// measure_done event の receive diagnostics 部分ペイロード構築ヘルパー。
    /// live 型 (UdpTransport / Subscription) を知らず、値オブジェクトだけを受け取る。
    /// </summary>
    internal static class PerfDiagnosticsBuilder
    {
        internal static IDictionary<string, object> BuildReceive(
            PerfTransportDiagnostics userUnicast,
            PerfTransportDiagnostics userMulticast,
            PerfSubscriptionDiagnostics subscription)
        {
            var fields = new Dictionary<string, object>();
            AddTransport(fields, "user_unicast", userUnicast);
            AddTransport(fields, "user_multicast", userMulticast);

            fields["subscription_payloads_from_reader"] = subscription.PayloadsReceivedFromReader;
            fields["subscription_messages_deserialized"] = subscription.MessagesDeserialized;
            fields["subscription_deserialize_failures"] = subscription.DeserializeFailures;
            fields["subscription_handler_invocations"] = subscription.HandlerInvocations;
            fields["rtps_data_submessages_received"] = subscription.DataSubmessagesReceived;
            fields["rtps_datafrag_submessages_received"] = subscription.DataFragSubmessagesReceived;
            fields["rtps_reassembled_payloads"] = subscription.ReassembledPayloads;
            fields["rtps_payloads_delivered"] = subscription.PayloadsDelivered;
            fields["rtps_payloads_buffered_pending_match"] = subscription.PayloadsBufferedPendingMatch;
            fields["rtps_payloads_dropped"] = subscription.PayloadsDropped;
            return fields;
        }

        private static void AddTransport(
            IDictionary<string, object> fields,
            string prefix,
            PerfTransportDiagnostics transport)
        {
            fields[prefix + "_transport_diagnostics_available"] = transport.Available;
            if (!transport.Available) return;
            fields[prefix + "_udp_datagrams_received"] = transport.DatagramsReceived;
            fields[prefix + "_udp_datagrams_enqueued"] = transport.DatagramsEnqueued;
            fields[prefix + "_udp_datagrams_dropped"] = transport.DatagramsDropped;
            fields[prefix + "_udp_datagrams_dispatched"] = transport.DatagramsDispatched;
            fields[prefix + "_udp_queue_count"] = transport.QueueCount;
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfDiagnosticsBuilderTests` 4 ケース pass。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/PerfDiagnosticsBuilderTests.cs \
        Ros2Unity/Assets/Perf/PerfDiagnosticsBuilder.cs
git commit -m "feat(perf): PerfDiagnosticsBuilder.BuildReceive を追加 (TDD)"
```

---

## Task 8: `StopwatchPerfClock` + `StopwatchWrapper` (TDD)

**Files:**
- Create: `Ros2Unity/Assets/Perf/StopwatchWrapper.cs`
- Create: `Ros2Unity/Assets/Perf/StopwatchPerfClock.cs`
- Create: `Ros2Unity/Assets/Tests/EditMode/StopwatchPerfClockTests.cs` (任意、Task 1 の asmdef が無いため test asmdef に追加)

注: テストファイルは既存 `PerfHarnessRegressionTests.cs` と同じアセンブリに含める。新規ファイル追加で十分。

- [ ] **Step 1: `StopwatchWrapper.cs` を作成**

`Ros2Unity/Assets/Perf/StopwatchWrapper.cs`:

```csharp
using System;
using System.Diagnostics;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// System.Diagnostics.Stopwatch を IPerfStopwatch にラップする adapter。
    /// Dispose は using 構文対応のため (Stopwatch 自体は IDisposable ではないため no-op)。
    /// </summary>
    internal sealed class StopwatchWrapper : IPerfStopwatch
    {
        private readonly Stopwatch _stopwatch;

        internal StopwatchWrapper(Stopwatch stopwatch)
        {
            _stopwatch = stopwatch ?? throw new ArgumentNullException(nameof(stopwatch));
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;
        public void Stop() => _stopwatch.Stop();
        public void Dispose() { /* no-op: Stopwatch は native resource を保持しない */ }
    }
}
```

- [ ] **Step 2: `StopwatchPerfClock.cs` を作成**

`Ros2Unity/Assets/Perf/StopwatchPerfClock.cs`:

```csharp
using System.Diagnostics;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// 実 Stopwatch を使う IPerfClock 実装。
    /// </summary>
    internal sealed class StopwatchPerfClock : IPerfClock
    {
        public IPerfStopwatch Start() => new StopwatchWrapper(Stopwatch.StartNew());
    }
}
```

- [ ] **Step 3: テスト追加 (任意、step 4 の統合テストで間接的にカバーされる)**

`Ros2Unity/Assets/Tests/EditMode/PerfHarnessRegressionTests.cs` の `PerfHarnessRegressionTests` クラス末尾に追加:

```csharp
        [Test]
        public void StopwatchPerfClock_measures_elapsed_time()
        {
            var clock = new StopwatchPerfClock();
            var sw = clock.Start();
            System.Threading.Thread.Sleep(10);
            Assert.GreaterOrEqual(sw.Elapsed.TotalMilliseconds, 5.0d);
            sw.Stop();
        }

        [Test]
        public void StopwatchWrapper_Stop_does_not_throw()
        {
            var clock = new StopwatchPerfClock();
            var sw = clock.Start();
            sw.Stop();
            sw.Stop(); // 2 回呼んでも例外なし
        }
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: `PerfHarnessRegressionTests` 5 ケース pass (既存 3 + 追加 2)。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/StopwatchWrapper.cs \
        Ros2Unity/Assets/Perf/StopwatchPerfClock.cs \
        Ros2Unity/Assets/Tests/EditMode/PerfHarnessRegressionTests.cs
git commit -m "feat(perf): StopwatchPerfClock / StopwatchWrapper (IPerfClock 実装) を追加"
```

---

## Task 9: `PerfMetricsWriter` を `IPerfMetricsSink` 実装に refactor

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfMetricsWriter.cs`

注: 既存テスト (PerfHarnessRegressionTests 等) はこのクラスに直接触れていないため、リファクタ後 EditMode テストで `PerfJson` 経由の挙動が等価であることを担保する。

- [ ] **Step 1: `PerfMetricsWriter.cs` を以下に置換**

`Ros2Unity/Assets/Perf/PerfMetricsWriter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class PerfMetricsWriter : IPerfMetricsSink
    {
        private readonly PerfPlayerArguments _args;
        private readonly StreamWriter _writer;

        internal PerfMetricsWriter(PerfPlayerArguments args)
        {
            _args = args;
            string directory = Path.GetDirectoryName(args.MetricsFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            _writer = new StreamWriter(args.MetricsFile, false, new UTF8Encoding(false));
            _writer.AutoFlush = true;
        }

        internal void Event(string name, IDictionary<string, object> fields = null)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            PerfJson.WriteString(builder, "event", name, first: true);
            PerfJson.WriteString(builder, "scenario", _args.Scenario);
            PerfJson.WriteString(builder, "direction", _args.DirectionName);
            PerfJson.WriteString(builder, "qos", _args.QosName);
            PerfJson.WriteNumber(builder, "payload_bytes", _args.PayloadBytes);
            PerfJson.WriteNumber(builder, "messages", _args.Messages);
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    PerfJson.WriteValue(builder, pair.Key, pair.Value);
                }
            }
            builder.Append('}');
            string line = builder.ToString();
            _writer.WriteLine(line);
            Debug.Log(line);
        }

        internal void WriteSentinel(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }
}
```

注: `internal void Event` は `IPerfMetricsSink.Event` を実装 (interface も `internal` なのでアクセス互換)。
`internal void WriteSentinel` も同様。

- [ ] **Step 2: 既存 EditMode テストが pass することを確認 (回帰確認)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: 全テスト pass (PerfJsonTests 16 / MeasureDoneBuilderTests 8 / PerfDiagnosticsBuilderTests 4 / PerfHarnessRegressionTests 5 = 33 ケース)。

- [ ] **Step 3: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/PerfMetricsWriter.cs
git commit -m "refactor(perf): PerfMetricsWriter を PerfJson 経由 + IPerfMetricsSink 実装に書き換え"
```

---

## Task 10: `PerfProfilerRecorders` を `IPerfProfilerSampler` 実装に refactor

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs`

- [ ] **Step 1: クラス宣言に interface 実装を追加**

`Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs` の `internal sealed class PerfProfilerRecorders : IDisposable` を `internal sealed class PerfProfilerRecorders : IPerfProfilerSampler` に変更 (1 行)。

- [ ] **Step 2: テストが pass することを確認 (回帰確認)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -20
```

期待: 全テスト pass (33 ケース)。

- [ ] **Step 3: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/PerfProfilerRecorders.cs
git commit -m "refactor(perf): PerfProfilerRecorders を IPerfProfilerSampler 実装に"
```

---

## Task 11: `PerfPlayerEntry.Run*` の signature 変更

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs`

注: 大きな signature 変更。本タスクは signature のみ変更し、本体内ロジックは次タスク (12 / 13) で builder 呼び出しに置換する。`Run` メソッドも更新して新 signature を呼ぶ形にする。

- [ ] **Step 1: `Run` メソッドを更新**

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` の `Run` メソッド (line 31-79) を以下に置換:

```csharp
        private static async Task Run(string[] args)
        {
            try
            {
                if (!PerfPlayerArguments.TryParse(args, out var parsed, out string error))
                {
                    UnityEngine.Debug.LogError(error);
                    Quit(1);
                    return;
                }

                using (var sink = new PerfMetricsWriter(parsed))
                {
                    try
                    {
                        sink.Event("start");
                        using (var sampler = parsed.ProfilerMode == PerfProfilerMode.Full
                            ? PerfProfilerRecorders.StartFull()
                            : PerfProfilerRecorders.StartLean())
                        {
                            IPerfClock clock = new StopwatchPerfClock();
                            if (parsed.Direction == PerfDirection.UnityToRos2)
                            {
                                using (var participant = CreateParticipant(parsed, "player_pub"))
                                {
                                    await RunUnityToRos2(parsed, participant, sink, sampler, clock);
                                }
                            }
                            else
                            {
                                using (var participant = CreateParticipant(parsed, "player_sub"))
                                {
                                    await RunRos2ToUnity(parsed, participant, sink, sampler, clock);
                                }
                            }
                        }
                        await WaitForRelease(parsed, sink);
                        sink.WriteSentinel(parsed.DoneFile, "ok");
                        sink.Event("done");
                        Quit(0);
                    }
                    catch (Exception ex)
                    {
                        sink.Event("error", new Dictionary<string, object> { { "message", ex.ToString() } });
                        sink.WriteSentinel(parsed.DoneFile, "error");
                        Quit(1);
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(ex);
                Quit(1);
            }
        }
```

注: 旧 `metrics.Event("start")` (line 46) を `sink.Event("start")` に置換。`using (var participant = ...)` は `Run` メソッド側で participant を所有・dispose する形に整理。**`Run*` メソッドは participant を dispose しない (呼び出し元責任)**。

- [ ] **Step 2: `RunUnityToRos2` / `RunRos2ToUnity` の signature を変更**

`RunUnityToRos2` (line 81) を以下に置換:

```csharp
        internal static async Task RunUnityToRos2(
            PerfPlayerArguments args,
            DomainParticipant participant,
            IPerfMetricsSink sink,
            IPerfProfilerSampler sampler,
            IPerfClock clock)
        {
            using (var publisher = participant.CreatePublisher<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       args.Reliability,
                       DurabilityQos.Volatile))
            {
                participant.Start();
                sink.WriteSentinel(args.ReadyFile, "ready");
                sink.Event("ready");

                bool matched = await publisher.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));
                if (!matched)
                {
                    throw new TimeoutException("Publisher did not match a ROS 2 subscriber.");
                }

                sink.Event("matched");
                var message = CreatePayloadMessage(args.PayloadBytes);
                int serializedBytes = publisher.SerializeWithEncapsulation(message).Length;

                using (IPerfStopwatch sw = clock.Start())
                {
                    sink.Event("measure_start");
                    await publisher.PublishRepeatedAsync(message, args.Messages);
                    sw.Stop();

                    IDictionary<string, object> profilerFields = sampler.Snapshot();
                    IDictionary<string, object> fields = MeasureDoneBuilder.BuildPublish(
                        sw.Elapsed, args.Messages, serializedBytes, profilerFields);
                    sink.Event("measure_done", fields);
                }
            }
        }
```

注: 旧 `using (participant)` (line 86) は **削除**。participant は引数で受けるが、ライフサイクルは呼び出し元 (Run メソッド) の責任。これにより EditMode テストから渡された `pair.Writer` を二重 dispose しない。

`RunRos2ToUnity` (line 121) を以下に置換:

```csharp
        internal static async Task RunRos2ToUnity(
            PerfPlayerArguments args,
            DomainParticipant participant,
            IPerfMetricsSink sink,
            IPerfProfilerSampler sampler,
            IPerfClock clock)
        {
            int received = 0;
            IPerfStopwatch sw = null;
            using (var subscription = participant.CreateSubscription<StringMessage>(
                       args.Topic,
                       StringMessageSerializer.Instance,
                       _ =>
                       {
                           if (Interlocked.Increment(ref received) == 1)
                           {
                               sw = clock.Start();
                           }
                       },
                       reliability: args.Reliability))
            {
                participant.Start();
                sink.WriteSentinel(args.ReadyFile, "ready");
                sink.Event("ready");

                bool matched = await subscription.WaitForMatchedAsync(1, TimeSpan.FromSeconds(20));
                if (!matched)
                {
                    throw new TimeoutException("Subscription did not match a ROS 2 publisher.");
                }
                sink.Event("matched");
                sink.Event("measure_start");

                bool completed = await AsyncReceiveWaiter.WaitUntilAsync(
                    () => Volatile.Read(ref received) >= args.Messages,
                    TimeSpan.FromSeconds(30),
                    async delay =>
                    {
                        sampler.Collect();
                        await Task.Delay(delay);
                    });
                if (!completed)
                {
                    sink.Event("receive_diagnostics", BuildReceiveDiagnostics(participant, subscription));
                    throw new TimeoutException(
                        "Timed out waiting for ROS 2 messages: received " +
                        Volatile.Read(ref received) + "/" + args.Messages + ".");
                }
                sw?.Stop();

                var message = CreatePayloadMessage(args.PayloadBytes);
                int serializedBytes = CdrEncapsulation.Size + StringMessageSerializer.Instance.GetSerializedSize(message);
                IDictionary<string, object> profilerFields = sampler.Snapshot();
                IDictionary<string, object> diagnostics = CollectReceiveDiagnostics(participant, subscription);
                IDictionary<string, object> fields = MeasureDoneBuilder.BuildSubscribe(
                    sw?.Elapsed ?? TimeSpan.Zero, received, serializedBytes, profilerFields, diagnostics);
                sink.Event("measure_done", fields);
            }
        }
```

注: 同じく旧 `using (participant)` (line 128) を削除。

- [ ] **Step 3: 補助メソッドを helper 経由に置換**

`AddReceiveDiagnostics` / `AddTransportDiagnostics` / `BuildReceiveDiagnostics` を残し、`CollectReceiveDiagnostics` (live 型から値オブジェクトへ写像) を新規追加:

```csharp
        private static IDictionary<string, object> BuildReceiveDiagnostics(
            DomainParticipant participant,
            Subscription<StringMessage> subscription)
        {
            return PerfDiagnosticsBuilder.BuildReceive(
                CollectTransport(participant.UserUnicastTransport),
                CollectTransport(participant.UserMulticastTransport),
                CollectSubscription(subscription));
        }

        private static IDictionary<string, object> CollectReceiveDiagnostics(
            DomainParticipant participant,
            Subscription<StringMessage> subscription)
        {
            return BuildReceiveDiagnostics(participant, subscription);
        }

        private static PerfTransportDiagnostics CollectTransport(IRtpsTransport transport)
        {
            if (transport is not UdpTransport udp)
            {
                return PerfTransportDiagnostics.Unavailable();
            }
            var d = udp.Diagnostics;
            return new PerfTransportDiagnostics(
                available: true,
                datagramsReceived: d.DatagramsReceived,
                datagramsEnqueued: d.DatagramsEnqueued,
                datagramsDropped: d.DatagramsDropped,
                datagramsDispatched: d.DatagramsDispatched,
                queueCount: d.QueueCount);
        }

        private static PerfSubscriptionDiagnostics CollectSubscription(Subscription<StringMessage> subscription)
        {
            var d = subscription.Diagnostics;
            var rtps = d.RtpsReader;
            return new PerfSubscriptionDiagnostics(
                payloadsReceivedFromReader: d.PayloadsReceivedFromReader,
                messagesDeserialized: d.MessagesDeserialized,
                deserializeFailures: d.DeserializeFailures,
                handlerInvocations: d.HandlerInvocations,
                dataSubmessagesReceived: rtps.DataSubmessagesReceived,
                dataFragSubmessagesReceived: rtps.DataFragSubmessagesReceived,
                reassembledPayloads: rtps.ReassembledPayloads,
                payloadsDelivered: rtps.PayloadsDelivered,
                payloadsBufferedPendingMatch: rtps.PayloadsBufferedPendingMatch,
                payloadsDropped: rtps.PayloadsDropped);
        }
```

`AddReceiveDiagnostics` / `AddTransportDiagnostics` の旧メソッドは **削除** (内部呼び出し箇所は Task 11 / 12 で置換済み)。`BuildReceiveDiagnostics` は `receive_diagnostics` event 用に残る。

- [ ] **Step 4: コンパイルが通ることを確認**

Unity Editor の Compile 完了を待機 (uloop / batchmode)。`Assets/Perf/ROSettaDDS.UnityPerfHarness.asmdef` 配下でコンパイルエラーが出ないこと。

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch 2>&1 | tail -30
```

期待: EditMode テスト実行が走り、`ROSettaDDS.UnityVerification.Tests` 関連のエラーが 0 件。

- [ ] **Step 5: 既存テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch 2>&1 | tail -20
```

期待: 33 ケース + 既存 ROSettaDDSUnityVerificationTests すべて pass。

- [ ] **Step 6: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/PerfPlayerEntry.cs
git commit -m "refactor(perf): Run* の signature を 5 引数 (participant + 3 interface) に変更"
```

---

## Task 12: NDJSON 出力が現行と完全互換であることを手動確認

注: 統合テスト (`PerfRunFlowTests`) は Task 14 / 15 で追加する。本タスクは **既存の動作が手元 (可能なら `dotnet run --project tools/rosettadds-perf-runner` で) 確認できること** が要件。Unity Editor 上で Perf Player をビルドし、`--skip-build` で 1 scenario 実行 → NDJSON を baseline と diff。

**Files:**
- (no file changes)

- [ ] **Step 1: 既存 baseline NDJSON をバックアップ**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
ls artifacts/perf/ | tail -3
# 直近の run-id をメモ (例: 20260628-153000)
BASELINE_RUN=20260628-153000  # ← 実値に置換
mkdir -p /tmp/perf-baseline
cp artifacts/perf/$BASELINE_RUN/*/metrics.ndjson /tmp/perf-baseline/ 2>/dev/null || echo "no baseline metrics"
```

注: `BASELINE_RUN` は直近の run-id ディレクトリのいずれかを指定。なければ本タスクはスキップ可 (次タスクの統合テストで担保)。

- [ ] **Step 2: 1 scenario を実行して NDJSON を取得**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
# 1) Player をリビルド (新コード含む)
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/refactor-build", "StandaloneLinux64", "mono"); return "ok";'
# 2) 1 scenario のみ実行
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/refactor-build/ROSettaDDSPerfPlayer \
  --scenario unity-to-ros2-reliable-32 --capture-frames 600
# 3) 直近の run-id の NDJSON を取得
NEW_RUN=$(ls -t artifacts/perf/ | head -1)
echo "new run: $NEW_RUN"
```

- [ ] **Step 3: baseline と diff (キー名の一致確認)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
# 各 NDJSON のキー名セットを比較 (値は timing jitter で異なるので比較しない)
NEW_DIR=artifacts/perf/$NEW_RUN/unity-to-ros2-reliable-32
for f in $NEW_DIR/metrics.ndjson; do
  echo "=== $f ==="
  # 各行の key のみ抽出 (":" の前)
  cat "$f" | tr ',' '\n' | sed 's/:.*//' | sort -u
done
```

期待: event vocabulary (`event`, `scenario`, `direction`, `qos`, `payload_bytes`, `messages`, `elapsed_ms`, `sent`, `serialized_bytes_per_message`, `serialized_bytes`, `messages_per_second`, `*_available`, `*_last`) が baseline と一致すること。

- [ ] **Step 4: コミット (差分なし、verification タスク完了の squash 用)**

差分なしなら commit 不要。次の Task に進む。

---

## Task 13: Fake テストインフラ

**Files:**
- Create: `Ros2Unity/Assets/Tests/EditMode/PerfHarnessFakes.cs`

- [ ] **Step 1: Fake クラスを作成**

`Ros2Unity/Assets/Tests/EditMode/PerfHarnessFakes.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    /// <summary>
    /// EditMode テスト用の IPerfMetricsSink fake。
    /// 出力された event と sentinel を記録する。
    /// </summary>
    internal sealed class FakePerfMetricsSink : IPerfMetricsSink
    {
        public List<(string Name, IDictionary<string, object> Fields)> Events { get; } = new();
        public List<(string Path, string Content)> Sentinels { get; } = new();

        public void Event(string name, IDictionary<string, object> fields = null)
        {
            Events.Add((name, fields ?? new Dictionary<string, object>()));
        }

        public void WriteSentinel(string path, string content)
        {
            Sentinels.Add((path, content));
        }

        public void Dispose() { }
    }

    /// <summary>
    /// EditMode テスト用の IPerfProfilerSampler fake。
    /// 設定可能な Snapshot 戻り値を持つ。
    /// </summary>
    internal sealed class FakePerfProfilerSampler : IPerfProfilerSampler
    {
        public int CollectCallCount { get; private set; }
        public IDictionary<string, object> NextSnapshot { get; set; }
            = new Dictionary<string, object>();

        public void Collect() => CollectCallCount++;

        public IDictionary<string, object> Snapshot() => NextSnapshot;
        public void Dispose() { }
    }

    /// <summary>
    /// EditMode テスト用の IPerfClock fake。設定可能な Elapsed 値を返す。
    /// </summary>
    internal sealed class FakePerfClock : IPerfClock
    {
        public TimeSpan Elapsed { get; set; }
        public IPerfStopwatch Start() => new FakePerfStopwatch(this);
    }

    /// <summary>
    /// EditMode テスト用の IPerfStopwatch fake。
    /// </summary>
    internal sealed class FakePerfStopwatch : IPerfStopwatch
    {
        private readonly FakePerfClock _clock;
        public FakePerfStopwatch(FakePerfClock clock) { _clock = clock; }
        public TimeSpan Elapsed => _clock.Elapsed;
        public void Stop() { }
    }
}
```

- [ ] **Step 2: ビルドが通ることを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch 2>&1 | tail -10
```

期待: コンパイルエラーなし。

- [ ] **Step 3: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/PerfHarnessFakes.cs
git commit -m "test(perf): FakePerfMetricsSink/ProfilerSampler/Clock を追加"
```

---

## Task 14: `PerfRunFlowTests` 統合テスト (TDD)

**Files:**
- Create: `Ros2Unity/Assets/Tests/EditMode/PerfRunFlowTests.cs`

注: 既存 `ROSettaDDSUnityVerificationTests.cs` の `LoopbackParticipantPair` パターン (`UnityLoopbackTestSupport.CreatePair()`) を使う。`PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` を fake sink/sampler/clock で実行する。

- [ ] **Step 1: 失敗するテストを書く**

`Ros2Unity/Assets/Tests/EditMode/PerfRunFlowTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfRunFlowTests
    {
        private static string TempFile(string name)
            => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "perf-test-" + System.Guid.NewGuid().ToString("N") + "-" + name);

        private static PerfPlayerArguments MakeArgs(PerfDirection direction, int messages = 8, int payload = 32)
        {
            string[] args =
            {
                "--rosettadds-perf",
                "--rosettadds-scenario", "test",
                "--rosettadds-direction", direction == PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity",
                "--rosettadds-domain-id", "0",
                "--rosettadds-topic", "/test_topic",
                "--rosettadds-qos", "reliable",
                "--rosettadds-payload-bytes", payload.ToString(),
                "--rosettadds-messages", messages.ToString(),
                "--rosettadds-ready-file", TempFile("ready"),
                "--rosettadds-done-file", TempFile("done"),
                "--rosettadds-metrics-file", TempFile("metrics.ndjson"),
            };
            Assert.IsTrue(PerfPlayerArguments.TryParse(args, out var parsed, out var err), err);
            return parsed;
        }

        [Test]
        public async Task RunUnityToRos2_emits_measure_done_with_expected_fields()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>
                {
                    { "main_thread_time_ns_available", true },
                    { "main_thread_time_ns_last", 100L },
                },
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.FromMilliseconds(500) };
            var args = MakeArgs(PerfDirection.UnityToRos2, messages: 4);

            // RunRos2ToUnity は participant を dispose しない (Task 11 参照)。
            // ので、counterparty subscriber を pre-set して matching を通す。
            int dummyReceived = 0;
            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                args.Topic, StringMessageSerializer.Instance, _ => Interlocked.Increment(ref dummyReceived));

            await PerfPlayerEntry.RunUnityToRos2(args, pair.Writer, sink, sampler, clock);

            var measureDone = sink.Events.FirstOrDefault(e => e.Name == "measure_done");
            Assert.IsNotNull(measureDone.Name);
            var f = measureDone.Fields;
            Assert.AreEqual(4L, f["sent"]);
            Assert.AreEqual(500.0d, f["elapsed_ms"]);
            Assert.IsTrue(f.ContainsKey("serialized_bytes_per_message"));
            Assert.IsTrue(f.ContainsKey("serialized_bytes"));
            Assert.IsTrue(f.ContainsKey("messages_per_second"));
            Assert.AreEqual(true, f["main_thread_time_ns_available"]);
        }

        [Test]
        public async Task RunRos2ToUnity_emits_measure_done_with_diagnostics()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>(),
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.FromSeconds(1) };
            var args = MakeArgs(PerfDirection.Ros2ToUnity, messages: 4);

            // Pre-set publisher on Writer side, then publish 4 messages.
            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                args.Topic, StringMessageSerializer.Instance);
            // Wait for SEDP match so messages will be delivered.
            // (matching requires both sides to be started; pair.Start() was called inside CreatePair)
            for (int i = 0; i < 4; i++)
            {
                await pub.PublishAsync(new StringMessage("test-" + i));
            }

            await PerfPlayerEntry.RunRos2ToUnity(args, pair.Reader, sink, sampler, clock);

            var measureDone = sink.Events.FirstOrDefault(e => e.Name == "measure_done");
            Assert.IsNotNull(measureDone.Name);
            var f = measureDone.Fields;
            // subscription が事前 publish した分も受け取るため、>= 4
            Assert.GreaterOrEqual((long)f["received"], 4L);
            Assert.IsTrue(f.ContainsKey("subscription_messages_deserialized"));
            // loopback transport は UdpTransport ではないため transport_diagnostics_available = false
            Assert.AreEqual(false, f["user_unicast_transport_diagnostics_available"]);
            Assert.AreEqual(false, f["user_multicast_transport_diagnostics_available"]);
        }

        [Test, Explicit("Slow: 20+ seconds due to hardcoded match/receive timeouts")]
        public void RunRos2ToUnity_timeout_emits_receive_diagnostics_and_throws()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>(),
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.Zero };
            var args = MakeArgs(PerfDirection.Ros2ToUnity, messages: 1000);

            // Pre-set publisher for matching, but don't publish → receive will timeout
            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                args.Topic, StringMessageSerializer.Instance);

            Assert.ThrowsAsync<TimeoutException>(async () =>
                await PerfPlayerEntry.RunRos2ToUnity(args, pair.Reader, sink, sampler, clock));

            // receive_diagnostics event が出力されているはず
            var rxDiag = sink.Events.FirstOrDefault(e => e.Name == "receive_diagnostics");
            Assert.IsNotNull(rxDiag.Name);
        }

        [Test]
        public void PerfMetricsWriter_escapes_payload_in_event_name()
        {
            // PerfPlayerArguments.TryParse は scenario 値にどんな文字も通す。
            // escape されることを確認。
            string[] args =
            {
                "--rosettadds-perf",
                "--rosettadds-scenario", "with\"quote\nnewline",
                "--rosettadds-direction", "unity_to_ros2",
                "--rosettadds-domain-id", "0",
                "--rosettadds-topic", "/t",
                "--rosettadds-qos", "reliable",
                "--rosettadds-payload-bytes", "32",
                "--rosettadds-messages", "1",
                "--rosettadds-ready-file", TempFile("ready"),
                "--rosettadds-done-file", TempFile("done"),
                "--rosettadds-metrics-file", TempFile("metrics-escape.ndjson"),
            };
            Assert.IsTrue(PerfPlayerArguments.TryParse(args, out var parsed, out var err), err);
            using (var writer = new PerfMetricsWriter(parsed))
            {
                writer.Event("start");
            }
            string line = System.IO.File.ReadAllText(parsed.MetricsFile).Trim();
            // scenario 値に " と \n が含まれていた場合、エスケープされているはず
            StringAssert.Contains("\\\"", line);
            StringAssert.Contains("\\n", line);
        }
    }
}
```

注: 上記テストは `PerfPlayerEntry.RunUnityToRos2` / `RunRos2ToUnity` が `internal static` であり、テストアセンブリからアクセスできることが前提 (Task 11 で `internal` 化済)。Test 3 (`RunRos2ToUnity_timeout_*`) は `[Explicit]` でマークし、CI 高速化のためデフォルトでは skip。`bash scripts/unity/run_editmode.sh --batch --filter-type regex --filter-value RunRos2ToUnity_timeout` で個別実行可能。

- [ ] **Step 2: テストを実行 (EditMode)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch --filter-type assembly --filter-value ROSettaDDS.UnityVerification.Tests 2>&1 | tail -30
```

期待: `PerfRunFlowTests` のうち Test 1, 2, 4 が pass (Test 3 は Explicit なので skip)。既存 33 ケース + 3 ケース = 36 ケース pass。

- [ ] **Step 3: 失敗時のデバッグ**

Test 1 / 2 が loopback で成立しない場合 (例: SEDP match に時間がかかりすぎる、`measure_done` の sent/received が予期せぬ値) は、Debug ログを有効にして `sink.Events` の中身を逐次確認する。失敗時は helper 単体テスト (`MeasureDoneBuilder.BuildPublish/Subscribe` への直接呼出) で担保する旨を findings に記録。

- [ ] **Step 4: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Tests/EditMode/PerfRunFlowTests.cs
git commit -m "test(perf): PerfRunFlowTests (Run* 統合) を追加 (TDD)"
```

---

## Task 15: 受け入れ基準の最終確認

**Files:**
- (no file changes, verification のみ)

- [ ] **Step 1: 既存 .NET unit test が緑であることを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tools/rosettadds-perf-runner.Tests/ 2>&1 | tail -10
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj 2>&1 | tail -10
```

期待: 両プロジェクト全テスト pass。

- [ ] **Step 2: Unity EditMode 全テストが緑であることを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash scripts/unity/run_editmode.sh --batch 2>&1 | tail -20
```

期待: 既存 + 新規 37 ケース pass。

- [ ] **Step 3: NDJSON 互換性確認 (Task 12 の続き)**

Task 12 で取得した NDJSON を baseline と diff する。**`measure_done` event の key セットが完全一致** することを目視確認。

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
NEW_RUN=$(ls -t artifacts/perf/ | head -1)
NEW_DIR=artifacts/perf/$NEW_RUN/unity-to-ros2-reliable-32
cat $NEW_DIR/metrics.ndjson | tr ',' '\n' | sed 's/:.*//' | sort -u
# 期待されるキー:
# elapsed_ms, event, main_thread_time_ns_available, main_thread_time_ns_last,
# messages, messages_per_second, payload_bytes, qos, scenario, direction,
# serialized_bytes, serialized_bytes_per_message, sent
```

期待: 上記 13 キーが全て含まれる (順序不問)。

- [ ] **Step 4: Unity メタファイル確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
bash .github/scripts/check_unity_meta.sh 2>&1 | tail -20
```

期待: 新規 .cs ファイルに .meta ファイルが存在し、orphan がないこと。Unity Editor を開いていないと .meta が生成されない場合は、Unity Editor を 1 度 batchmode 起動して生成する:

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
# Unity batchmode で Ros2Unity を開く (Assets/ をスキャンして .meta を生成)
# 具体的なコマンドは .opencode / common.sh の UNITY_EDITOR 設定に依存
# 例:
"$UNITY_EDITOR" -batchmode -nographics -projectPath Ros2Unity -quit
```

- [ ] **Step 5: 変更のコミット (もし .meta ファイルを生成した場合)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git status
git add '*.meta'  # 新規 .meta のみ add される
git commit -m "chore(unity): 新規ファイル群の .meta を追加" || echo "no new .meta files"
```

- [ ] **Step 6: ブランチ確認と push (任意)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git log --oneline main..HEAD
git push origin perf/unity-perf-harness-testability  # リモートに push
```

- [ ] **Step 7: PR 作成の説明 (任意、AGENTS.md の方針に従う)**

PR 説明文のドラフト:

```
## 概要
Ros2Unity/Assets/Perf/ 配下を、純関数ヘルパー + interface 抽象中心の設計に再構成。
EditMode から直接テストできる構造にし、perf run event 出力 (measure_done) の
構造を assert できるようにする。

## 変更内容
- 4 interface 追加 (IPerfMetricsSink/ProfilerSampler/Clock/Stopwatch)
- 3 純関数ヘルパー追加 (PerfJson/MeasureDoneBuilder/PerfDiagnosticsBuilder)
- 2 値オブジェクト追加 (PerfTransportDiagnostics/PerfSubscriptionDiagnostics)
- 1 実装 (StopwatchPerfClock/StopwatchWrapper)
- PerfPlayerEntry.Run* の signature を 5 引数に変更
- EditMode テスト 5 ファイル追加 (計 33 → 37 ケース)

## 受け入れ基準
- [x] 既存 + 新規 EditMode テスト 37 ケース pass
- [x] NDJSON event vocabulary / measure_done キー名 完全互換
- [x] 公開 API への破壊的変更なし
- [x] check_unity_meta.sh クリーン

## 参照
- spec: docs/superpowers/specs/2026-06-29-unity-perf-harness-testability-design.md
- plan: docs/superpowers/plans/2026-06-29-unity-perf-harness-testability.md
```

---

## 補足: タスク全体のサマリ

| Task | 内容 | 種別 | ケース追加 |
|---:|---|---|---:|
| 1 | 4 interface 作成 | foundation | 0 |
| 2 | PerfJson.Escape | TDD | +9 |
| 3 | PerfJson.Write* | TDD | +7 |
| 4 | MeasureDoneBuilder.BuildPublish | TDD | +5 |
| 5 | MeasureDoneBuilder.BuildSubscribe | TDD | +3 |
| 6 | 値オブジェクト 2 種 | plain | 0 |
| 7 | PerfDiagnosticsBuilder.BuildReceive | TDD | +4 |
| 8 | StopwatchPerfClock / Wrapper | TDD | +2 |
| 9 | PerfMetricsWriter refactor | refactor | 0 |
| 10 | PerfProfilerRecorders refactor | refactor | 0 |
| 11 | Run* signature 変更 | refactor | 0 |
| 12 | NDJSON 互換性確認 | verify | 0 |
| 13 | Fake インフラ | foundation | 0 |
| 14 | PerfRunFlowTests | TDD | +3 (1 [Explicit] skip) |
| 15 | 最終確認 | verify | 0 |
| | **合計** | | **+33** |

(既存 PerfHarnessRegressionTests 3 → 5 への +2 を含む Task 8 の +2 を含む)

最終的な EditMode テスト数: 既存 (Unity Verification) + 既存 PerfHarnessRegression 3 + 新規 PerfJson 16 + MeasureDoneBuilder 8 + PerfDiagnosticsBuilder 4 + PerfHarnessRegression 増分 2 + PerfRunFlow 3 (fast) + PerfRunFlow 1 (Explicit) = **約 +33 ケース**。

注: 仕様書 (spec) の 7.1 節では PerfRunFlowTests を 4 ケースとしていたが、Task 14 で timeout test を `[Explicit]` 化したため、デフォルト実行では 3 ケース + Explicit 1 ケース。CI 高速化のため、Explicit は手動実行。
