using System;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfPlayerArgumentsTests
    {
        private static string[] Args(params string[] a) => a;

        [Test]
        public void LocalhostOnly_未指定なら_true()
        {
            Assert.IsTrue(PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-domain-id", "0",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2"),
                out var parsed, out _));
            Assert.IsTrue(parsed.LocalhostOnly);
        }

        [Test]
        public void LocalhostOnly_false_を_受理する()
        {
            Assert.IsTrue(PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-domain-id", "0",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2",
                     "--rosettadds-localhost-only", "false"),
                out var parsed, out _));
            Assert.IsFalse(parsed.LocalhostOnly);
        }

        [Test]
        public void StaticPeer_指定が_保持される()
        {
            Assert.IsTrue(PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-domain-id", "0",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2",
                     "--rosettadds-static-peer", "192.168.0.20"),
                out var parsed, out _));
            Assert.AreEqual("192.168.0.20", parsed.StaticPeer);
        }

        [Test]
        public void StaticPeer_未指定なら_null()
        {
            Assert.IsTrue(PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-domain-id", "0",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2"),
                out var parsed, out _));
            Assert.IsNull(parsed.StaticPeer);
        }

        [Test]
        public void LocalhostOnly_true_を_受理する()
        {
            Assert.IsTrue(PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-domain-id", "0",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2",
                     "--rosettadds-localhost-only", "true"),
                out var parsed, out _));
            Assert.IsTrue(parsed.LocalhostOnly);
        }
    }
}
