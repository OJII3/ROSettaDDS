using System;
using System.Collections.Generic;
using System.Globalization;
using ROSettaDDS.Dds.QoS;

namespace ROSettaDDS.UnityPerfHarness
{
    internal enum PerfDirection
    {
        UnityToRos2,
        Ros2ToUnity,
    }

    internal enum PerfQos
    {
        Reliable,
        BestEffort,
    }

    internal sealed class PerfPlayerArguments
    {
        private PerfPlayerArguments()
        {
        }

        internal string Scenario { get; private set; }
        internal PerfDirection Direction { get; private set; }
        internal int DomainId { get; private set; }
        internal string Topic { get; private set; }
        internal PerfQos Qos { get; private set; }
        internal int PayloadBytes { get; private set; }
        internal int Messages { get; private set; }
        internal string ReadyFile { get; private set; }
        internal string DoneFile { get; private set; }
        internal string MetricsFile { get; private set; }
        internal string ReleaseFile { get; private set; }

        internal ReliabilityQos Reliability
            => Qos == PerfQos.BestEffort ? ReliabilityQos.BestEffort : ReliabilityQos.Reliable;

        internal string DirectionName
            => Direction == PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity";

        internal string QosName
            => Qos == PerfQos.BestEffort ? "best_effort" : "reliable";

        internal static bool IsPerfRun(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--rosettadds-perf")
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool TryParse(string[] args, out PerfPlayerArguments parsed, out string error)
        {
            parsed = null;
            error = null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool enabled = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--rosettadds-perf")
                {
                    enabled = true;
                    continue;
                }

                if (!arg.StartsWith("--rosettadds-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    error = "missing value for " + arg;
                    return false;
                }
                values[arg] = args[++i];
            }

            if (!enabled)
            {
                error = "--rosettadds-perf is required";
                return false;
            }

            var result = new PerfPlayerArguments();
            if (!ReadRequired(values, "--rosettadds-scenario", out string scenario, out error)) return false;
            if (!ReadDirection(values, out PerfDirection direction, out error)) return false;
            if (!ReadInt(values, "--rosettadds-domain-id", positive: false, out int domainId, out error)) return false;
            if (!ReadRequired(values, "--rosettadds-topic", out string topic, out error)) return false;
            if (!ReadQos(values, out PerfQos qos, out error)) return false;
            if (!ReadInt(values, "--rosettadds-payload-bytes", positive: true, out int payloadBytes, out error)) return false;
            if (!ReadInt(values, "--rosettadds-messages", positive: true, out int messages, out error)) return false;
            if (!ReadRequired(values, "--rosettadds-ready-file", out string readyFile, out error)) return false;
            if (!ReadRequired(values, "--rosettadds-done-file", out string doneFile, out error)) return false;
            if (!ReadRequired(values, "--rosettadds-metrics-file", out string metricsFile, out error)) return false;
            values.TryGetValue("--rosettadds-release-file", out string releaseFile);

            if (string.IsNullOrEmpty(topic) || topic[0] != '/')
            {
                error = "--rosettadds-topic must be an absolute ROS topic";
                return false;
            }

            result.Scenario = scenario;
            result.Direction = direction;
            result.DomainId = domainId;
            result.Topic = topic;
            result.Qos = qos;
            result.PayloadBytes = payloadBytes;
            result.Messages = messages;
            result.ReadyFile = readyFile;
            result.DoneFile = doneFile;
            result.MetricsFile = metricsFile;
            result.ReleaseFile = releaseFile;
            parsed = result;
            return true;
        }

        private static bool ReadRequired(
            Dictionary<string, string> values, string key, out string value, out string error)
        {
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                error = key + " is required";
                return false;
            }
            error = null;
            return true;
        }

        private static bool ReadInt(
            Dictionary<string, string> values, string key, bool positive, out int value, out string error)
        {
            value = 0;
            if (!ReadRequired(values, key, out string raw, out error))
            {
                return false;
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = key + " must be an integer";
                return false;
            }
            if (positive && value <= 0)
            {
                error = key + " must be positive";
                return false;
            }
            if (!positive && value < 0)
            {
                error = key + " must be zero or positive";
                return false;
            }
            return true;
        }

        private static bool ReadDirection(
            Dictionary<string, string> values, out PerfDirection direction, out string error)
        {
            direction = PerfDirection.UnityToRos2;
            if (!ReadRequired(values, "--rosettadds-direction", out string raw, out error))
            {
                return false;
            }
            if (raw == "unity_to_ros2")
            {
                direction = PerfDirection.UnityToRos2;
                return true;
            }
            if (raw == "ros2_to_unity")
            {
                direction = PerfDirection.Ros2ToUnity;
                return true;
            }
            error = "--rosettadds-direction must be unity_to_ros2 or ros2_to_unity";
            return false;
        }

        private static bool ReadQos(Dictionary<string, string> values, out PerfQos qos, out string error)
        {
            qos = PerfQos.Reliable;
            if (!ReadRequired(values, "--rosettadds-qos", out string raw, out error))
            {
                return false;
            }
            if (raw == "reliable")
            {
                qos = PerfQos.Reliable;
                return true;
            }
            if (raw == "best_effort")
            {
                qos = PerfQos.BestEffort;
                return true;
            }
            error = "--rosettadds-qos must be reliable or best_effort";
            return false;
        }
    }
}
