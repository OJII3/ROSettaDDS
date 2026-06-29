using System.Text;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfJsonTests
    {
        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("plain", "plain")]
        [TestCase("a", "a")]
        [TestCase("a\\b", "a\\\\b")]
        [TestCase("a\"b", "a\\\"b")]
        [TestCase("a\nb", "a\\nb")]
        [TestCase("a\rb", "a\\rb")]
        [TestCase("\\\\", "\\\\\\\\")]
        public void Escape_replaces_json_special_characters(string input, string expected)
        {
            Assert.AreEqual(expected, PerfJson.Escape(input));
        }
    }
}
