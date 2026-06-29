using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
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
            fields["messages_per_second"] = received / Math.Max(0.000001d, elapsed.TotalSeconds);
            if (diagnostics != null)
            {
                foreach (var kv in diagnostics)
                {
                    fields[kv.Key] = kv.Value;
                }
            }
            return fields;
        }
    }
}
