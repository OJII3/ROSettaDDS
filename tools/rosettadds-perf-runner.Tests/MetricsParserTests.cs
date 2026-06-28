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

    [Fact]
    public void ParseMeasureDone_は_予期しない_shape_の_JSON_を_スキップする()
    {
        string path = Path.Combine(Path.GetTempPath(), "metrics-parser-test-shape.ndjson");
        File.WriteAllText(path,
            "[]\n" +
            "123\n" +
            "{\"event\":123}\n" +
            "{\"event\":\"measure_done\",\"received\":10.5}\n" +
            "{\"event\":\"measure_done\",\"messages_per_second\":42.0}\n");
        try
        {
            var result = MetricsParser.ParseMeasureDone(path);
            result.Should().NotBeNull();
            result!.MessagesPerSecond.Should().Be(42.0);
            result.Received.Should().Be(0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
