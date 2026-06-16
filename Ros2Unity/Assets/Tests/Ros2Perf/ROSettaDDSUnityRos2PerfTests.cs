using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using Unity.PerformanceTesting;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class ROSettaDDSUnityRos2PerfTests
    {
        // FastDDS は RTPS port を 7400 + 250 * domainId + ... で計算するため、
        // domain id が大きいとポート計算が overflow して
        // "Calculated port number is too high" で reject される。
        // ROS_LOCALHOST_ONLY=1 で loopback 限定なので、低い domain id を安全に使える。
        // s_domainSequence は 0 から始まり Interlocked.Increment で 1 以上になるので、
        // 実際に使われる id は BaseDomainId + 1 以降 (例: 1, 2, 3, ...)。
        private const int BaseDomainId = 0;
        private static int s_domainSequence;
        private static int s_topicSequence;

        private static readonly Ros2PerfScenario[] Scenarios =
        {
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.Reliable, 32, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.Reliable, 1024, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.UnityToRos2, Ros2PerfQos.BestEffort, 8192, 2, 200),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.Reliable, 32, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.Reliable, 1024, 1, 500),
            new Ros2PerfScenario(Ros2PerfDirection.Ros2ToUnity, Ros2PerfQos.BestEffort, 8192, 2, 200),
        };

        [UnityTest]
        [Performance]
        public IEnumerator ROS_2_loopback_perf_を記録する()
        {
            if (!Ros2PerfHelperProcess.IsAvailable())
            {
                Assert.Ignore("ROS 2 perf helper not found: " + Ros2PerfHelperProcess.ResolveExecutablePath());
            }

            for (int i = 0; i < Scenarios.Length; i++)
            {
                yield return RunScenario(Scenarios[i]);
            }
        }

        private static IEnumerator RunScenario(Ros2PerfScenario scenario)
        {
            if (scenario.Direction == Ros2PerfDirection.UnityToRos2)
            {
                yield return RunUnityToRos2(scenario);
            }
            else
            {
                yield return RunRos2ToUnity(scenario);
            }
        }

        private static IEnumerator RunUnityToRos2(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            var helpers = new List<Ros2PerfHelperProcess>();
            try
            {
                for (int i = 0; i < scenario.Fanout; i++)
                {
                    string args = "--mode sub --topic " + topic
                        + " --messages " + scenario.MessageCount
                        + " --payload-bytes " + scenario.PayloadBytes
                        + " --rate-hz 0 --qos " + scenario.QosArgument
                        + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
                    helpers.Add(Ros2PerfHelperProcess.Start(args, domainId, scenario.QosArgument));
                }

                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Ready, TimeSpan.FromSeconds(10), out _, out var error), error);
                }

                ForceFullCollection();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                long monoBefore = Profiler.GetMonoUsedSizeLong();

                using var participant = CreateParticipant(domainId, "unity_pub");
                using var publisher = participant.CreatePublisher<StringMessage>(
                    topic,
                    StringMessageSerializer.Instance,
                    ToReliability(scenario.Qos),
                    DurabilityQos.Volatile);
                participant.Start();

                var message = CreatePayloadMessage(scenario.PayloadBytes);
                int serializedBytes = publisher.SerializeWithEncapsulation(message).Length;
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < scenario.MessageCount; i++)
                {
                    publisher.PublishAsync(message).GetAwaiter().GetResult();
                }

                int received = 0;
                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Done, TimeSpan.FromSeconds(20), out var done, out var error), error);
                    received += done.Received;
                }
                stopwatch.Stop();

                ForceFullCollection();
                RecordMetrics(scenario, stopwatch.Elapsed, scenario.MessageCount * scenario.Fanout, serializedBytes, managedBefore, monoBefore);
                Assert.AreEqual(scenario.MessageCount * scenario.Fanout, received);
            }
            finally
            {
                for (int i = helpers.Count - 1; i >= 0; i--) helpers[i].Dispose();
            }
            yield return null;
        }

        private static IEnumerator RunRos2ToUnity(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            int received = 0;
            var helpers = new List<Ros2PerfHelperProcess>();

            try
            {
                // 受信側を helper 起動より前に用意しないと、helper が送る先頭メッセージを逃すため、
                // participant / subscription は try の先頭で確保する (RunUnityToRos2 と順序が
                // 逆になるのはこのプロトコル上の都合)。
                using var participant = CreateParticipant(domainId, "unity_sub");
                using var subscription = participant.CreateSubscription<StringMessage>(
                    topic,
                    StringMessageSerializer.Instance,
                    _ => Interlocked.Increment(ref received),
                    reliability: ToReliability(scenario.Qos));
                participant.Start();

                ForceFullCollection();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                long monoBefore = Profiler.GetMonoUsedSizeLong();

                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < scenario.Fanout; i++)
                {
                    string args = "--mode pub --topic " + topic
                        + " --messages " + scenario.MessageCount
                        + " --payload-bytes " + scenario.PayloadBytes
                        + " --rate-hz 0 --qos " + scenario.QosArgument
                        + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
                    helpers.Add(Ros2PerfHelperProcess.Start(args, domainId, scenario.QosArgument));
                }

                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Done, TimeSpan.FromSeconds(20), out _, out var error), error);
                }

                int expected = scenario.MessageCount * scenario.Fanout;
                yield return WaitUntil(() => Volatile.Read(ref received) >= expected, TimeSpan.FromSeconds(20));
                stopwatch.Stop();

                // helper 側が送信する payload と同じサイズの StringMessage を
                // 一時シリアライズして、encap header 込みの 1 message あたり byte 数を実測する。
                var message = CreatePayloadMessage(scenario.PayloadBytes);
                int serializedBytes = CdrEncapsulation.Size + StringMessageSerializer.Instance.GetSerializedSize(message);

                ForceFullCollection();
                RecordMetrics(scenario, stopwatch.Elapsed, expected, serializedBytes, managedBefore, monoBefore);
                int finalReceived = Volatile.Read(ref received);
                Assert.AreEqual(expected, finalReceived);
            }
            finally
            {
                for (int i = helpers.Count - 1; i >= 0; i--) helpers[i].Dispose();
            }
        }

        private static DomainParticipant CreateParticipant(int domainId, string entityName)
            => new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = domainId,
                ParticipantId = 0,
                EntityName = "rosettadds_ros2_perf_" + entityName,
                LocalhostOnly = true,
                SpdpInterval = TimeSpan.FromMilliseconds(100),
                SedpInterval = TimeSpan.FromMilliseconds(100),
                UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(100),
            });

        private static ReliabilityQos ToReliability(Ros2PerfQos qos)
            => qos == Ros2PerfQos.BestEffort ? ReliabilityQos.BestEffort : ReliabilityQos.Reliable;

        private static int NextDomainId()
            => BaseDomainId + Interlocked.Increment(ref s_domainSequence);

        private static StringMessage CreatePayloadMessage(int payloadBytes)
        {
            string prefix = "unity-";
            string data = prefix + new string('x', Math.Max(1, payloadBytes - prefix.Length));
            return new StringMessage(data);
        }

        private static void RecordMetrics(
            Ros2PerfScenario scenario,
            TimeSpan elapsed,
            int deliveredMessages,
            int serializedBytesPerMessage,
            long managedBefore,
            long monoBefore)
        {
            double elapsedMs = Math.Max(0.001d, elapsed.TotalMilliseconds);
            double elapsedSeconds = Math.Max(0.000001d, elapsed.TotalSeconds);
            string prefix = scenario.GroupPrefix;
            Measure.Custom(new SampleGroup(prefix + "elapsed_ms", SampleUnit.Millisecond, false), elapsedMs);
            Measure.Custom(new SampleGroup(prefix + "messages_per_second", SampleUnit.Undefined, true), deliveredMessages / elapsedSeconds);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_second", SampleUnit.Undefined, true), deliveredMessages * serializedBytesPerMessage / elapsedSeconds);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_message", SampleUnit.Byte, false), serializedBytesPerMessage);
            Measure.Custom(new SampleGroup(prefix + "managed_heap_delta_bytes", SampleUnit.Byte, false), PositiveDelta(GC.GetTotalMemory(forceFullCollection: true), managedBefore));
            Measure.Custom(new SampleGroup(prefix + "unity_mono_used_delta_bytes", SampleUnit.Byte, false), PositiveDelta(Profiler.GetMonoUsedSizeLong(), monoBefore));
        }

        private static IEnumerator WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + timeout.TotalSeconds;
            while (UnityEngine.Time.realtimeSinceStartupAsDouble < deadline && !condition())
            {
                yield return null;
            }
        }

        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static long PositiveDelta(long after, long before)
            => Math.Max(0L, after - before);
    }
}
