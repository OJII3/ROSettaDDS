using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Cdr;
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
        // helper は ROS 2 RTPS port を 7400 + 250 * domainId + ... で計算するため、
        // domain id が大きいと "Calculated port number is too high" で reject される。
        // ROS_LOCALHOST_ONLY=1 の loopback では低い id を安全に使える。
        private const int BaseDomainId = 0;
        private const int WarmupBurst = 50;
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
                string envValue = System.Environment.GetEnvironmentVariable("ROSETTADDS_ROS2_PERF_HELPER");
                Assert.Ignore("ROSETTADDS_ROS2_PERF_HELPER が未設定 (現在値: " +
                    (envValue ?? "<null>") + ")。nix develop シェル内で実行するか " +
                    "scripts/ros2/build_helper.sh で helper を build して環境変数を export してください。");
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

        // Unity 側 publisher → ROS 側 subscriber
        private static IEnumerator RunUnityToRos2(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            var helpers = new List<Ros2PerfHelperProcess>();
            int totalDelivered = 0;
            int totalExpected = 0;

            try
            {
                // 1. helper (sub) 起動 + ready 待ち
                for (int i = 0; i < scenario.Fanout; i++)
                {
                    string args = "--mode sub --topic " + topic
                        + " --messages " + (WarmupBurst + scenario.MessageCount)
                        + " --payload-bytes " + scenario.PayloadBytes
                        + " --rate-hz 0 --qos " + scenario.QosArgument
                        + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
                    helpers.Add(Ros2PerfHelperProcess.Start(args, domainId, scenario.QosArgument));
                }
                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Ready, TimeSpan.FromSeconds(10), out _, out var error), error);
                }

                // 2. Unity 側 participant + publisher を setup
                using var participant = CreateParticipant(domainId, "unity_pub");
                using var publisher = participant.CreatePublisher<StringMessage>(
                    topic,
                    StringMessageSerializer.Instance,
                    ToReliability(scenario.Qos),
                    DurabilityQos.Volatile);
                participant.Start();
                yield return WaitForRemoteReader(participant, TopicName(topic), TimeSpan.FromSeconds(10));

                // 3. 計測対象の message と 1 件あたり byte 数 (両方向で同じ式)
                var message = CreatePayloadMessage(scenario.PayloadBytes);
                int serializedBytesPerMessage = publisher.SerializeWithEncapsulation(message).Length;

                // 4. ウォームアップ (JIT, ヒープ, 内部キャッシュ warm)
                for (int i = 0; i < WarmupBurst; i++)
                {
                    publisher.PublishAsync(message).GetAwaiter().GetResult();
                }
                // ウォームアップ分の message が相手側に届くまで数 frame 待つ
                for (int i = 0; i < 10; i++)
                {
                    yield return null;
                }

                // 5. 計測 (steady-state バーストを Stopwatch で計時)
                ForceFullCollection();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                long monoBefore = Profiler.GetMonoUsedSizeLong();
                int measurementCount = scenario.MessageCount;
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < measurementCount; i++)
                {
                    publisher.PublishAsync(message).GetAwaiter().GetResult();
                }
                foreach (var helper in helpers)
                {
                    Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Done, TimeSpan.FromSeconds(20), out var done, out var error), error);
                    totalDelivered += done.Received;
                }
                stopwatch.Stop();
                long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
                long monoAfter = Profiler.GetMonoUsedSizeLong();
                totalExpected = measurementCount * scenario.Fanout + WarmupBurst * scenario.Fanout;

                RecordMetrics(
                    scenario,
                    stopwatch.Elapsed,
                    measurementCount,
                    scenario.Fanout,
                    totalDelivered,
                    totalExpected,
                    serializedBytesPerMessage,
                    managedAfter - managedBefore,
                    monoAfter - monoBefore);

                AssertReliableDelivery(scenario, totalDelivered, totalExpected);
            }
            finally
            {
                for (int i = helpers.Count - 1; i >= 0; i--) helpers[i].Dispose();
            }
            yield return null;
        }

        // ROS 側 publisher → Unity 側 subscriber
        private static IEnumerator RunRos2ToUnity(Ros2PerfScenario scenario)
        {
            int domainId = NextDomainId();
            string topic = "/rosettadds_perf_" + Interlocked.Increment(ref s_topicSequence);
            int received = 0;
            Ros2PerfHelperProcess helper = null;

            try
            {
                // 1. Unity 側 participant + subscription を helper 起動前に用意
                //    (先に subscription がないと helper が送る先頭メッセージを取り逃す)
                using var participant = CreateParticipant(domainId, "unity_sub");
                using var subscription = participant.CreateSubscription<StringMessage>(
                    topic,
                    StringMessageSerializer.Instance,
                    msg => Interlocked.Increment(ref received),
                    reliability: ToReliability(scenario.Qos));
                received = 0;
                participant.Start();

                // 2. 1 件あたり wire size は両方向とも wire 上 (encap header + payload) で
                //    同一。serializer の size 取得だけで DDS endpoint を作らずに済む。
                var message = CreatePayloadMessage(scenario.PayloadBytes);
                int serializedBytesPerMessage = CdrEncapsulation.Size
                    + StringMessageSerializer.Instance.GetSerializedSize(message);

                // 3. helper を --measure-start 付きで起動。ready → (subscriber discovery
                //    待ち) → armed → stdin 受信 で publish ループに入る。
                //    これにより計測範囲に process spawn / rclcpp init / SPDP/SEDP を
                //    含めず、Unity 側 stopwatch は publish burst のみを計る。
                ForceFullCollection();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                long monoBefore = Profiler.GetMonoUsedSizeLong();
                int measurementCount = scenario.MessageCount;
                int beforeMeasure = received;
                helper = Ros2PerfHelperProcess.Start(
                    BuildHelperArgs(topic, measurementCount, scenario) + " --measure-start",
                    domainId,
                    scenario.QosArgument);
                Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Ready, TimeSpan.FromSeconds(10), out _, out var readyErr), readyErr);
                Assert.IsTrue(helper.TryWaitForEvent(Ros2PerfHelperEventKind.Armed, TimeSpan.FromSeconds(10), out _, out var armedErr), armedErr);
                // armed 時点で helper は stdin 待ち。SEDP は ready → armed の間に交換済み。
                yield return WaitForRemoteWriter(participant, TopicName(topic), TimeSpan.FromSeconds(10));

                var stopwatch = Stopwatch.StartNew();
                helper.SendMeasureStart();
                int expected = beforeMeasure + measurementCount * scenario.Fanout;
                yield return WaitUntil(() => Volatile.Read(ref received) >= expected, TimeSpan.FromSeconds(20));
                stopwatch.Stop();
                long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
                long monoAfter = Profiler.GetMonoUsedSizeLong();

                int totalDelivered = received;
                int totalExpected = expected;

                RecordMetrics(
                    scenario,
                    stopwatch.Elapsed,
                    measurementCount,
                    scenario.Fanout,
                    totalDelivered,
                    totalExpected,
                    serializedBytesPerMessage,
                    managedAfter - managedBefore,
                    monoAfter - monoBefore);

                AssertReliableDelivery(scenario, totalDelivered, totalExpected);
            }
            finally
            {
                helper?.Dispose();
            }
        }

        private static string TopicName(string topic)
            => "rt/" + topic.TrimStart('/');

        private static string BuildHelperArgs(string topic, int count, Ros2PerfScenario scenario)
        {
            return "--mode pub --topic " + topic
                + " --messages " + count
                + " --payload-bytes " + scenario.PayloadBytes
                + " --rate-hz 0 --qos " + scenario.QosArgument
                + " --ready-timeout-ms 5000 --idle-timeout-ms 5000";
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
            int measurementMessages,
            int fanout,
            int totalDelivered,
            int totalExpected,
            int serializedBytesPerMessage,
            long managedHeapDelta,
            long monoUsedDelta)
        {
            double elapsedMs = Math.Max(0.001d, elapsed.TotalMilliseconds);
            double elapsedSeconds = Math.Max(0.000001d, elapsed.TotalSeconds);
            double messagesPerSecond = measurementMessages * fanout / elapsedSeconds;
            double bytesPerSecond = messagesPerSecond * serializedBytesPerMessage;
            double deliveryRate = totalExpected > 0 ? (double)totalDelivered / totalExpected : 1.0;

            string prefix = scenario.GroupPrefix;
            Measure.Custom(new SampleGroup(prefix + "elapsed_ms", SampleUnit.Millisecond, false), elapsedMs);
            Measure.Custom(new SampleGroup(prefix + "messages_per_second", SampleUnit.Undefined, true), messagesPerSecond);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_second", SampleUnit.Undefined, true), bytesPerSecond);
            Measure.Custom(new SampleGroup(prefix + "serialized_bytes_per_message", SampleUnit.Byte, false), serializedBytesPerMessage);
            Measure.Custom(new SampleGroup(prefix + "delivery_rate", SampleUnit.Undefined, true), deliveryRate);
            Measure.Custom(new SampleGroup(prefix + "managed_heap_delta_bytes", SampleUnit.Byte, false), Math.Max(0L, managedHeapDelta));
            Measure.Custom(new SampleGroup(prefix + "unity_mono_used_delta_bytes", SampleUnit.Byte, false), Math.Max(0L, monoUsedDelta));
        }

        private static void AssertReliableDelivery(
            Ros2PerfScenario scenario,
            int delivered,
            int expected)
        {
            if (scenario.Qos != Ros2PerfQos.Reliable)
            {
                return;
            }
            Assert.AreEqual(
                expected,
                delivered,
                $"Reliable delivery 損失: {delivered}/{expected}。loopback + matched 完了後の損失は本来発生しない");
        }

        private static IEnumerator WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + timeout.TotalSeconds;
            while (UnityEngine.Time.realtimeSinceStartupAsDouble < deadline && !condition())
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForRemoteReader(DomainParticipant participant, string ddsTopic, TimeSpan timeout)
        {
            yield return WaitUntil(
                () => participant.DiscoveryDb.ReaderSnapshot().Any(ep => ep.TopicName == ddsTopic),
                timeout);
        }

        private static IEnumerator WaitForRemoteWriter(DomainParticipant participant, string ddsTopic, TimeSpan timeout)
        {
            yield return WaitUntil(
                () => participant.DiscoveryDb.WriterSnapshot().Any(ep => ep.TopicName == ddsTopic),
                timeout);
        }

        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
