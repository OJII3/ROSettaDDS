using System;
using System.Globalization;
using System.Text;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Perf Metrics 用の JSON 純関数ヘルパー。
    /// IL2CPP 起動時 overhead を避けるため手書き。System.Text.Json は使わない。
    /// 仕様: \\ → \\\\, \" → \\\", \n → \\n, \r → \\r の 4 種のみ置換。
    /// </summary>
    internal static class PerfJson
    {
        internal static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        internal static void WriteString(StringBuilder b, string key, string value, bool first = true)
        {
            if (!first) b.Append(',');
            b.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        internal static void WriteNumber(StringBuilder b, string key, long value, bool first = true)
        {
            AppendRaw(b, key, value.ToString(CultureInfo.InvariantCulture), first);
        }

        internal static void WriteBoolean(StringBuilder b, string key, bool value, bool first = true)
        {
            AppendRaw(b, key, value ? "true" : "false", first);
        }

        internal static void WriteValue(StringBuilder b, string key, object value, bool first = true)
        {
            if (value == null)
            {
                AppendRaw(b, key, "null", first);
            }
            else if (value is string text)
            {
                WriteString(b, key, text, first);
            }
            else if (value is bool flag)
            {
                AppendRaw(b, key, flag ? "true" : "false", first);
            }
            else if (value is int || value is long || value is float || value is double)
            {
                AppendRaw(b, key, Convert.ToString(value, CultureInfo.InvariantCulture), first);
            }
            else
            {
                WriteString(b, key, value.ToString(), first);
            }
        }

        private static void AppendRaw(StringBuilder b, string key, string value, bool first)
        {
            if (!first) b.Append(',');
            b.Append('"').Append(Escape(key)).Append("\":").Append(value);
        }
    }
}
