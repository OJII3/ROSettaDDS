using System;
using System.Globalization;
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
    public void MedianLong_は_1_run_でその値を返す()
    {
        RunAggregator.MedianLong(new long[] { 100L }).Should().Be(100L);
    }

    [Fact]
    public void MedianLong_は_奇数個で中央値()
    {
        RunAggregator.MedianLong(new long[] { 10L, 50L, 30L, 90L, 70L }).Should().Be(50L);
    }

    [Fact]
    public void MedianLong_は_偶数個で_整数除算()
    {
        // (1 + 2) / 2 = 1 (integer division)
        RunAggregator.MedianLong(new long[] { 1L, 2L }).Should().Be(1L);
        // (2 + 3) / 2 = 2
        RunAggregator.MedianLong(new long[] { 1L, 2L, 3L, 4L }).Should().Be(2L);
    }

    [Fact]
    public void MedianLong_は_空で_0()
    {
        RunAggregator.MedianLong(Array.Empty<long>()).Should().Be(0L);
    }

    [Fact]
    public void MedianLong_は_未ソート入力でも正しい()
    {
        RunAggregator.MedianLong(new long[] { 90L, 10L, 50L, 30L, 70L }).Should().Be(50L);
    }

    [Fact]
    public void Aggregate_は_N個のmetrics_pathから_全_8_メトリクスを_集計する()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "agg-test-all-metrics-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            WriteMeasureDoneFull(
                Path.Combine(tempDir, "repeat-00", "metrics.ndjson"),
                mps: 100, elapsed: 50, received: 500,
                mainThread: 80000, gcReserved: 1000, gcUsed: 2000, sysUsed: 3000, serBytes: 41);
            WriteMeasureDoneFull(
                Path.Combine(tempDir, "repeat-01", "metrics.ndjson"),
                mps: 200, elapsed: 25, received: 500,
                mainThread: 85000, gcReserved: 1500, gcUsed: 2500, sysUsed: 3500, serBytes: 41);
            WriteMeasureDoneFull(
                Path.Combine(tempDir, "repeat-02", "metrics.ndjson"),
                mps: 300, elapsed: 16, received: 500,
                mainThread: 90000, gcReserved: 2000, gcUsed: 3000, sysUsed: 4000, serBytes: 41);

            var runDirs = new[]
            {
                Path.Combine(tempDir, "repeat-00"),
                Path.Combine(tempDir, "repeat-01"),
                Path.Combine(tempDir, "repeat-02"),
            };
            var aggregate = RunAggregator.Aggregate(runDirs, AggregateKind.Median);
            aggregate.Should().NotBeNull();
            aggregate!.RunCount.Should().Be(3);
            aggregate.MessagesPerSecond.Should().Be(200.0);
            aggregate.ElapsedMs.Should().Be(25.0);
            aggregate.Received.Should().Be(500);
            aggregate.MainThreadTimeNsLast.Should().Be(85000);
            aggregate.GcReservedMemoryBytesLast.Should().Be(1500);
            aggregate.GcUsedMemoryBytesLast.Should().Be(2500);
            aggregate.SystemUsedMemoryBytesLast.Should().Be(3500);
            aggregate.SerializedBytesPerMessage.Should().Be(41);
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

    [Fact]
    public void Save_は_PascalCase_の_JSON_を書き出す()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "agg-save-test-" + Guid.NewGuid() + ".json");
        try
        {
            var metrics = new AggregateMetrics
            {
                RunCount = 3,
                MessagesPerSecond = 200.5,
                ElapsedMs = 25.5,
                Received = 500,
                MainThreadTimeNsLast = 85000,
                GcReservedMemoryBytesLast = 1500,
                GcUsedMemoryBytesLast = 2500,
                SystemUsedMemoryBytesLast = 3500,
                SerializedBytesPerMessage = 41,
            };
            RunAggregator.Save(tempPath, metrics);
            File.Exists(tempPath).Should().BeTrue();
            string content = File.ReadAllText(tempPath);
            content.Should().Contain("\"MessagesPerSecond\": 200.5");
            content.Should().Contain("\"RunCount\": 3");
            content.Should().Contain("\"MainThreadTimeNsLast\": 85000");
            content.Should().Contain("\"SerializedBytesPerMessage\": 41");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void WriteMeasureDone(string path, double mps, double elapsed, long received)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"event\":\"start\"}\n" +
            string.Format(CultureInfo.InvariantCulture,
                "{{\"event\":\"measure_done\",\"messages_per_second\":{0},\"elapsed_ms\":{1},\"received\":{2}}}\n",
                mps, elapsed, received) +
            "{\"event\":\"done\"}\n");
    }

    private static void WriteMeasureDoneFull(
        string path, double mps, double elapsed, long received,
        long mainThread, long gcReserved, long gcUsed, long sysUsed, long serBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"event\":\"start\"}\n" +
            string.Format(CultureInfo.InvariantCulture,
                "{{\"event\":\"measure_done\"," +
                "\"messages_per_second\":{0},\"elapsed_ms\":{1},\"received\":{2}," +
                "\"main_thread_time_ns_last\":{3}," +
                "\"gc_reserved_memory_bytes_last\":{4}," +
                "\"gc_used_memory_bytes_last\":{5}," +
                "\"system_used_memory_bytes_last\":{6}," +
                "\"serialized_bytes_per_message\":{7}}}\n",
                mps, elapsed, received, mainThread, gcReserved, gcUsed, sysUsed, serBytes) +
            "{\"event\":\"done\"}\n");
    }
}
