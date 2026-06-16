using NUnit.Framework;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class Ros2PerfHelperParserTests
    {
        [Test]
        public void TryParse_は_ready_event_を読む()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"ready\",\"mode\":\"sub\",\"topic\":\"/chatter\"}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Ready, parsed.Kind);
            Assert.AreEqual("sub", parsed.Mode);
            Assert.AreEqual("/chatter", parsed.Topic);
        }

        [Test]
        public void TryParse_は_done_event_の_received_と_elapsed_ms_を読む()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"done\",\"received\":42,\"elapsed_ms\":12.5}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Done, parsed.Kind);
            Assert.AreEqual(42, parsed.Received);
            Assert.AreEqual(12.5d, parsed.ElapsedMilliseconds);
        }

        [Test]
        public void TryParse_は_unknown_event_を失敗させる()
        {
            Assert.IsFalse(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"mystery\"}",
                out _,
                out var error));

            StringAssert.Contains("unknown event", error);
        }

        [Test]
        public void TryParse_は空行を失敗させる()
        {
            Assert.IsFalse(Ros2PerfHelperEvent.TryParse(
                "",
                out _,
                out var error));

            Assert.AreEqual("empty JSON line", error);
        }

        [Test]
        public void TryParse_は_event_未定義を失敗させる()
        {
            Assert.IsFalse(Ros2PerfHelperEvent.TryParse(
                "{\"mode\":\"pub\"}",
                out _,
                out var error));

            Assert.AreEqual("missing event property", error);
        }

        [Test]
        public void TryParse_は文字列の基本エスケープを戻す()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"error\",\"message\":\"line\\nquote\\\"slash\\\\tab\\treturn\\r\"}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Error, parsed.Kind);
            Assert.AreEqual("line\nquote\"slash\\tab\treturn\r", parsed.Message);
        }

        [Test]
        public void TryParse_は_progress_event_の_sent_を読む()
        {
            Assert.IsTrue(Ros2PerfHelperEvent.TryParse(
                "{\"event\":\"progress\",\"sent\":7}",
                out var parsed,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Ros2PerfHelperEventKind.Progress, parsed.Kind);
            Assert.AreEqual(7, parsed.Sent);
        }
    }
}
