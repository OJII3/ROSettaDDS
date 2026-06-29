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

        public void Event(string name, IDictionary<string, object> fields = null)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            PerfJson.WriteString(builder, "event", name, first: true);
            PerfJson.WriteString(builder, "scenario", _args.Scenario, first: false);
            PerfJson.WriteString(builder, "direction", _args.DirectionName, first: false);
            PerfJson.WriteString(builder, "qos", _args.QosName, first: false);
            PerfJson.WriteNumber(builder, "payload_bytes", _args.PayloadBytes, first: false);
            PerfJson.WriteNumber(builder, "messages", _args.Messages, first: false);
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    PerfJson.WriteValue(builder, pair.Key, pair.Value, first: false);
                }
            }
            builder.Append('}');
            string line = builder.ToString();
            _writer.WriteLine(line);
            Debug.Log(line);
        }

        public void WriteSentinel(string path, string content)
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
