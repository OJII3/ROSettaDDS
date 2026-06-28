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
