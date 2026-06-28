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
