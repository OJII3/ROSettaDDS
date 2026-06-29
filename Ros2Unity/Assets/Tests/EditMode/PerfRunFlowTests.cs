using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    public sealed class PerfRunFlowTests
    {
        private static string TempFile(string name)
            => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "perf-test-" + System.Guid.NewGuid().ToString("N") + "-" + name);

        private static PerfPlayerArguments MakeArgs(PerfDirection direction, int messages = 8, int payload = 32)
        {
            string[] args =
            {
                "--rosettadds-perf",
                "--rosettadds-scenario", "test",
                "--rosettadds-direction", direction == PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity",
                "--rosettadds-domain-id", "0",
                "--rosettadds-topic", "/test_topic",
                "--rosettadds-qos", "reliable",
                "--rosettadds-payload-bytes", payload.ToString(),
                "--rosettadds-messages", messages.ToString(),
                "--rosettadds-ready-file", TempFile("ready"),
                "--rosettadds-done-file", TempFile("done"),
                "--rosettadds-metrics-file", TempFile("metrics.ndjson"),
            };
            Assert.IsTrue(PerfPlayerArguments.TryParse(args, out var parsed, out var err), err);
            return parsed;
        }

        [Test]
        public async Task RunUnityToRos2_emits_measure_done_with_expected_fields()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>
                {
                    { "main_thread_time_ns_available", true },
                    { "main_thread_time_ns_last", 100L },
                },
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.FromMilliseconds(500) };
            var args = MakeArgs(PerfDirection.UnityToRos2, messages: 4);

            int dummyReceived = 0;
            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                args.Topic, StringMessageSerializer.Instance, _ => Interlocked.Increment(ref dummyReceived));
            pair.Reader.Start();

            await PerfPlayerEntry.RunUnityToRos2(args, pair.Writer, sink, sampler, clock);

            var measureDone = sink.Events.FirstOrDefault(e => e.Name == "measure_done");
            Assert.IsNotNull(measureDone.Name);
            var f = measureDone.Fields;
            Assert.AreEqual(4L, f["sent"]);
            Assert.AreEqual(500.0d, f["elapsed_ms"]);
            Assert.IsTrue(f.ContainsKey("serialized_bytes_per_message"));
            Assert.IsTrue(f.ContainsKey("serialized_bytes"));
            Assert.IsTrue(f.ContainsKey("messages_per_second"));
            Assert.AreEqual(true, f["main_thread_time_ns_available"]);
        }

        [Test]
        public async Task RunRos2ToUnity_emits_measure_done_with_diagnostics()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>(),
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.FromSeconds(1) };
            var args = MakeArgs(PerfDirection.Ros2ToUnity, messages: 4);

            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                args.Topic, StringMessageSerializer.Instance,
                ReliabilityQos.Reliable, DurabilityQos.TransientLocal);
            pair.Writer.Start();

            for (int i = 0; i < 4; i++)
            {
                await pub.PublishAsync(new StringMessage("test-" + i));
            }

            await PerfPlayerEntry.RunRos2ToUnity(args, pair.Reader, sink, sampler, clock);

            var measureDone = sink.Events.FirstOrDefault(e => e.Name == "measure_done");
            Assert.IsNotNull(measureDone.Name);
            var f = measureDone.Fields;
            Assert.GreaterOrEqual((int)f["received"], 4);
            Assert.IsTrue(f.ContainsKey("subscription_messages_deserialized"));
            Assert.AreEqual(false, f["user_unicast_transport_diagnostics_available"]);
            Assert.AreEqual(false, f["user_multicast_transport_diagnostics_available"]);
        }

        [Test, Explicit("Slow: 20+ seconds due to hardcoded match/receive timeouts")]
        public void RunRos2ToUnity_timeout_emits_receive_diagnostics_and_throws()
        {
            var pair = UnityLoopbackTestSupport.CreatePair();
            var sink = new FakePerfMetricsSink();
            var sampler = new FakePerfProfilerSampler
            {
                NextSnapshot = new Dictionary<string, object>(),
            };
            var clock = new FakePerfClock { Elapsed = TimeSpan.Zero };
            var args = MakeArgs(PerfDirection.Ros2ToUnity, messages: 1000);

            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                args.Topic, StringMessageSerializer.Instance);
            pair.Writer.Start();

            Assert.ThrowsAsync<TimeoutException>(async () =>
                await PerfPlayerEntry.RunRos2ToUnity(args, pair.Reader, sink, sampler, clock));

            var rxDiag = sink.Events.FirstOrDefault(e => e.Name == "receive_diagnostics");
            Assert.IsNotNull(rxDiag.Name);
        }

        [Test]
        public void PerfMetricsWriter_escapes_payload_in_event_name()
        {
            string[] args =
            {
                "--rosettadds-perf",
                "--rosettadds-scenario", "with\"quote\nnewline",
                "--rosettadds-direction", "unity_to_ros2",
                "--rosettadds-domain-id", "0",
                "--rosettadds-topic", "/t",
                "--rosettadds-qos", "reliable",
                "--rosettadds-payload-bytes", "32",
                "--rosettadds-messages", "1",
                "--rosettadds-ready-file", TempFile("ready"),
                "--rosettadds-done-file", TempFile("done"),
                "--rosettadds-metrics-file", TempFile("metrics-escape.ndjson"),
            };
            Assert.IsTrue(PerfPlayerArguments.TryParse(args, out var parsed, out var err), err);
            using (var writer = new PerfMetricsWriter(parsed))
            {
                writer.Event("start");
            }
            string line = System.IO.File.ReadAllText(parsed.MetricsFile).Trim();
            StringAssert.Contains("\\\"", line);
            StringAssert.Contains("\\n", line);
        }
    }
}
