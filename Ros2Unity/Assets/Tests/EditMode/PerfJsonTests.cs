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
        [Test]
        public void WriteString_writes_key_value_pair_with_quotes()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "v");
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_with_first_true_omits_leading_comma()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "v", first: true);
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_with_first_false_appends_leading_comma()
        {
            var sb = new StringBuilder("{");
            PerfJson.WriteString(sb, "k", "v", first: false);
            Assert.AreEqual("{,\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteString_escapes_special_characters()
        {
            var sb = new StringBuilder();
            PerfJson.WriteString(sb, "k", "a\"b\nc");
            Assert.AreEqual("\"k\":\"a\\\"b\\nc\"", sb.ToString());
        }

        [TestCase(42L, "\"k\":42")]
        [TestCase(-1L, "\"k\":-1")]
        [TestCase(0L, "\"k\":0")]
        public void WriteNumber_uses_invariant_culture(long value, string expected)
        {
            var sb = new StringBuilder();
            PerfJson.WriteNumber(sb, "k", value);
            Assert.AreEqual(expected, sb.ToString());
        }

        [Test]
        public void WriteBoolean_true()
        {
            var sb = new StringBuilder();
            PerfJson.WriteBoolean(sb, "k", true);
            Assert.AreEqual("\"k\":true", sb.ToString());
        }

        [Test]
        public void WriteBoolean_false()
        {
            var sb = new StringBuilder();
            PerfJson.WriteBoolean(sb, "k", false);
            Assert.AreEqual("\"k\":false", sb.ToString());
        }

        [Test]
        public void WriteValue_null_writes_null_literal()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", null);
            Assert.AreEqual("\"k\":null", sb.ToString());
        }

        [Test]
        public void WriteValue_string_writes_quoted()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)"v");
            Assert.AreEqual("\"k\":\"v\"", sb.ToString());
        }

        [Test]
        public void WriteValue_bool_writes_true_false()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)true);
            Assert.AreEqual("\"k\":true", sb.ToString());
        }

        [Test]
        public void WriteValue_long_writes_number()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)42L);
            Assert.AreEqual("\"k\":42", sb.ToString());
        }

        [Test]
        public void WriteValue_int_writes_number()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)42);
            Assert.AreEqual("\"k\":42", sb.ToString());
        }

        [Test]
        public void WriteValue_double_writes_invariant()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)3.14d);
            Assert.AreEqual("\"k\":3.14", sb.ToString());
        }

        [Test]
        public void WriteValue_unknown_type_falls_back_to_ToString()
        {
            var sb = new StringBuilder();
            PerfJson.WriteValue(sb, "k", (object)new { X = 1 });
            // object の ToString() は "TypeName" 形式
            StringAssert.StartsWith("\"k\":\"", sb.ToString());
            StringAssert.EndsWith("\"", sb.ToString());
        }
    }
}
