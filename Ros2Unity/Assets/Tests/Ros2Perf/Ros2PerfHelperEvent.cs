using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal enum Ros2PerfHelperEventKind
    {
        Ready,
        Progress,
        Done,
        Error,
    }

    internal readonly struct Ros2PerfHelperEvent
    {
        private static readonly Regex StringPropertyPattern = new Regex(
            "\"(?<name>[^\"]+)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");

        private static readonly Regex NumberPropertyPattern = new Regex(
            "\"(?<name>[^\"]+)\"\\s*:\\s*(?<value>-?[0-9]+(?:\\.[0-9]+)?)");

        private Ros2PerfHelperEvent(
            Ros2PerfHelperEventKind kind,
            string mode,
            string topic,
            string message,
            int received,
            int sent,
            double elapsedMilliseconds)
        {
            Kind = kind;
            Mode = mode;
            Topic = topic;
            Message = message;
            Received = received;
            Sent = sent;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        internal Ros2PerfHelperEventKind Kind { get; }
        internal string Mode { get; }
        internal string Topic { get; }
        internal string Message { get; }
        internal int Received { get; }
        internal int Sent { get; }
        internal double ElapsedMilliseconds { get; }

        internal static bool TryParse(string line, out Ros2PerfHelperEvent parsed, out string error)
        {
            parsed = default;
            error = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                error = "empty JSON line";
                return false;
            }

            string eventName = ReadString(line, "event");
            if (eventName == null)
            {
                error = "missing event property";
                return false;
            }

            if (!TryReadKind(eventName, out Ros2PerfHelperEventKind kind))
            {
                error = "unknown event: " + eventName;
                return false;
            }

            parsed = new Ros2PerfHelperEvent(
                kind,
                ReadString(line, "mode"),
                ReadString(line, "topic"),
                ReadString(line, "message"),
                ReadInt(line, "received"),
                ReadInt(line, "sent"),
                ReadDouble(line, "elapsed_ms"));
            return true;
        }

        private static bool TryReadKind(string eventName, out Ros2PerfHelperEventKind kind)
        {
            switch (eventName)
            {
                case "ready":
                    kind = Ros2PerfHelperEventKind.Ready;
                    return true;
                case "progress":
                    kind = Ros2PerfHelperEventKind.Progress;
                    return true;
                case "done":
                    kind = Ros2PerfHelperEventKind.Done;
                    return true;
                case "error":
                    kind = Ros2PerfHelperEventKind.Error;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        private static string ReadString(string line, string name)
        {
            foreach (Match match in StringPropertyPattern.Matches(line))
            {
                if (match.Groups["name"].Value == name)
                {
                    return Unescape(match.Groups["value"].Value);
                }
            }

            return null;
        }

        private static int ReadInt(string line, string name)
        {
            string value = ReadNumber(line, name);
            return value == null ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
        }

        private static double ReadDouble(string line, string name)
        {
            string value = ReadNumber(line, name);
            return value == null ? 0d : double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string ReadNumber(string line, string name)
        {
            foreach (Match match in NumberPropertyPattern.Matches(line))
            {
                if (match.Groups["name"].Value == name)
                {
                    return match.Groups["value"].Value;
                }
            }

            return null;
        }

        private static string Unescape(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(value[i]);
                    continue;
                }

                i++;
                switch (value[i])
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    default:
                        builder.Append('\\');
                        builder.Append(value[i]);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
