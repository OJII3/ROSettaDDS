using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class PerfMetricsWriter : IDisposable
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
            AppendString(builder, "event", name, first: true);
            AppendString(builder, "scenario", _args.Scenario);
            AppendString(builder, "direction", _args.DirectionName);
            AppendString(builder, "qos", _args.QosName);
            AppendNumber(builder, "payload_bytes", _args.PayloadBytes);
            AppendNumber(builder, "messages", _args.Messages);
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    AppendValue(builder, pair.Key, pair.Value);
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

        private static void AppendValue(StringBuilder builder, string key, object value)
        {
            if (value == null)
            {
                AppendRaw(builder, key, "null");
            }
            else if (value is string text)
            {
                AppendString(builder, key, text);
            }
            else if (value is bool flag)
            {
                AppendRaw(builder, key, flag ? "true" : "false");
            }
            else if (value is int || value is long || value is float || value is double)
            {
                AppendRaw(builder, key, Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else
            {
                AppendString(builder, key, value.ToString());
            }
        }

        private static void AppendString(StringBuilder builder, string key, string value, bool first = false)
        {
            if (!first) builder.Append(',');
            builder.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static void AppendNumber(StringBuilder builder, string key, long value)
        {
            AppendRaw(builder, key, value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendRaw(StringBuilder builder, string key, string value)
        {
            builder.Append(',').Append('"').Append(Escape(key)).Append("\":").Append(value);
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
