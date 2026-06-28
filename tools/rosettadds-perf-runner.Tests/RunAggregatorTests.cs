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
            WriteMeasureDone(Path.Combine(tempDir, "repeat-00", "metrics.ndjson"),
                mps: 100, elapsed: 50, received: 500);
            WriteMeasureDone(Path.Combine(tempDir, "repeat-01", "metrics.ndjson"),
                mps: 200, elapsed: 25, received: 500);
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
