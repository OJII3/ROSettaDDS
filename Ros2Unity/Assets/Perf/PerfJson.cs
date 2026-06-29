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
    }
}
