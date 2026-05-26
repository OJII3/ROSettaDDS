using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using Rclsharp.Common;
using Rclsharp.Dds;
using Rclsharp.Msgs.Std;
using Rclsharp.Transport;
using Unity.PerformanceTesting;
using UnityEngine.Profiling;

namespace Rclsharp.UnityVerification.Tests
{
    public sealed class RclsharpUnityVerificationTests
    {
        private const string SmokeTopic = "unity_chatter";
        private const string ThroughputTopicPrefix = "unity_throughput";
        private const string LeakTopicPrefix = "unity_leak";
        private const int SmokeMessageCount = 16;
        private const int WarmupMessageCount = 32;
        private const int ThroughputSampleCount = 5;
        private const int LeakCycleCount = 8;
        private const int LeakMessagesPerCycle = 256;
        private const long ManagedLeakThresholdBytes = 8L * 1024L * 1024L;
        private const long UnityMonoLeakThresholdBytes = 64L * 1024L * 1024L;
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
        private static readonly ThroughputScenario[] ThroughputScenarios =
        {
            new ThroughputScenario(32, 2000),
            new ThroughputScenario(1024, 1000),
            new ThroughputScenario(8192, 256),
        };
        private static int s_topicSequence;
        private static int s_participantPairSequence;

        [Test]
        public void Loopback_pubsub_で_StringMessage_を順序通り受信できる()
        {
            using var pair = CreateLoopbackPair();
            string topic = UniqueTopic(SmokeTopic);
            var received = new List<string>(SmokeMessageCount);
            var gate = new object();

            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (msg, _) =>
                {
                    lock (gate)
                    {
                        received.Add(msg.Data);
                    }
                });

            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance);

            pair.Start();
            AssertDiscovered(pair.Writer, pair.Reader, "rt/" + topic);

            for (int i = 0; i < SmokeMessageCount; i++)
            {
                pub.PublishAsync(new StringMessage("unity-smoke-" + i)).GetAwaiter().GetResult();
            }

            Assert.IsTrue(WaitUntil(() =>
            {
                lock (gate)
                {
                    return received.Count == SmokeMessageCount;
                }
            }, ReceiveTimeout), "Unity loopback smoke test timed out before all messages arrived.");

            lock (gate)
            {
                CollectionAssert.AreEqual(
                    Enumerable.Range(0, SmokeMessageCount).Select(i => "unity-smoke-" + i).ToArray(),
                    received);
            }
        }

        [Test]
        [Performance]
        public void Loopback_pubsub_の_payload別通信速度を記録する()
        {
            for (int i = 0; i < ThroughputScenarios.Length; i++)
            {
                RecordThroughputScenario(ThroughputScenarios[i]);
            }
        }

        [Test]
        [Performance]
        public void Loopback_pubsub_の反復実行後に_retained_memory_が閾値以内()
        {
            ForceFullCollection();
            long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
            long unityTotalAllocatedBefore = Profiler.GetTotalAllocatedMemoryLong();
            long unityMonoUsedBefore = Profiler.GetMonoUsedSizeLong();

            long managedBaseline = 0L;
            long unityMonoBaseline = 0L;
            long maxManagedRetained = 0L;
            long maxUnityMonoRetained = 0L;

            for (int cycle = 0; cycle < LeakCycleCount; cycle++)
            {
                RunLeakCycle(cycle);
                ForceFullCollection();

                long managedAfterCycle = GC.GetTotalMemory(forceFullCollection: true);
                long unityMonoAfterCycle = Profiler.GetMonoUsedSizeLong();

                if (cycle == 0)
                {
                    managedBaseline = managedAfterCycle;
                    unityMonoBaseline = unityMonoAfterCycle;
                    continue;
                }

                long managedRetained = PositiveDelta(managedAfterCycle, managedBaseline);
                long unityMonoRetained = PositiveDelta(unityMonoAfterCycle, unityMonoBaseline);
                maxManagedRetained = Math.Max(maxManagedRetained, managedRetained);
                maxUnityMonoRetained = Math.Max(maxUnityMonoRetained, unityMonoRetained);

                Measure.Custom(new SampleGroup("rclsharp.leak.cycle_managed_heap_retained_bytes", SampleUnit.Byte, false), managedRetained);
                Measure.Custom(new SampleGroup("rclsharp.leak.cycle_unity_mono_used_retained_bytes", SampleUnit.Byte, false), unityMonoRetained);
            }

            ForceFullCollection();
            long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
            long unityTotalAllocatedAfter = Profiler.GetTotalAllocatedMemoryLong();
            long unityMonoUsedAfter = Profiler.GetMonoUsedSizeLong();

            long finalManagedRetained = PositiveDelta(managedAfter, managedBaseline);
            long finalUnityMonoRetained = PositiveDelta(unityMonoUsedAfter, unityMonoBaseline);

            Measure.Custom(new SampleGroup("rclsharp.leak.managed_heap_retained_bytes", SampleUnit.Byte, false), finalManagedRetained);
            Measure.Custom(new SampleGroup("rclsharp.leak.managed_heap_max_retained_bytes", SampleUnit.Byte, false), maxManagedRetained);
            Measure.Custom(new SampleGroup("rclsharp.leak.unity_mono_used_retained_bytes", SampleUnit.Byte, false), finalUnityMonoRetained);
            Measure.Custom(new SampleGroup("rclsharp.leak.unity_mono_used_max_retained_bytes", SampleUnit.Byte, false), maxUnityMonoRetained);
            Measure.Custom(new SampleGroup("rclsharp.leak.unity_total_allocated_delta_bytes", SampleUnit.Byte, false), PositiveDelta(unityTotalAllocatedAfter, unityTotalAllocatedBefore));
            Measure.Custom(new SampleGroup("rclsharp.leak.managed_heap_total_delta_bytes", SampleUnit.Byte, false), PositiveDelta(managedAfter, managedBefore));
            Measure.Custom(new SampleGroup("rclsharp.leak.unity_mono_used_total_delta_bytes", SampleUnit.Byte, false), PositiveDelta(unityMonoUsedAfter, unityMonoUsedBefore));

            Assert.LessOrEqual(
                maxManagedRetained,
                ManagedLeakThresholdBytes,
                "Managed heap retained bytes exceeded the Unity leak guard threshold.");
            Assert.LessOrEqual(
                maxUnityMonoRetained,
                UnityMonoLeakThresholdBytes,
                "Unity mono used retained bytes exceeded the Unity leak guard threshold.");
        }

        private static void RecordThroughputScenario(ThroughputScenario scenario)
        {
            using var pair = CreateLoopbackPair();
            string topic = UniqueTopic(ThroughputTopicPrefix + "_" + scenario.PayloadBytes + "b");
            int received = 0;

            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (_, _) => Interlocked.Increment(ref received));

            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance);

            pair.Start();
            AssertDiscovered(pair.Writer, pair.Reader, "rt/" + topic);

            var message = CreatePayloadMessage("throughput", scenario.PayloadBytes);
            int serializedBytes = pub.SerializeWithEncapsulation(message).Length;

            PublishBatch(
                pub,
                message,
                Math.Min(WarmupMessageCount, scenario.MessageCount),
                () => Interlocked.Exchange(ref received, 0),
                () => Volatile.Read(ref received),
                "Throughput warmup messages were not received before timeout.");

            string groupPrefix = "rclsharp.throughput." + scenario.PayloadBytes + "B.";
            for (int sample = 0; sample < ThroughputSampleCount; sample++)
            {
                var result = PublishBatch(
                    pub,
                    message,
                    scenario.MessageCount,
                    () => Interlocked.Exchange(ref received, 0),
                    () => Volatile.Read(ref received),
                    "Throughput messages were not received before timeout.");

                RecordThroughputSample(groupPrefix, result, serializedBytes);
            }
        }

        private static void RunLeakCycle(int cycle)
        {
            using var pair = CreateLoopbackPair();
            string topic = UniqueTopic(LeakTopicPrefix + "_" + cycle);
            int received = 0;

            using var sub = pair.Reader.CreateSubscription<StringMessage>(
                topic,
                StringMessageSerializer.Instance,
                (_, _) => Interlocked.Increment(ref received));

            using var pub = pair.Writer.CreatePublisher<StringMessage>(
                topic,
                StringMessageSerializer.Instance);

            pair.Start();
            AssertDiscovered(pair.Writer, pair.Reader, "rt/" + topic);

            var message = CreatePayloadMessage("leak", 128);
            PublishBatch(
                pub,
                message,
                LeakMessagesPerCycle,
                () => Interlocked.Exchange(ref received, 0),
                () => Volatile.Read(ref received),
                "Leak guard messages were not received before timeout.");
        }

        private static PublishBatchResult PublishBatch(
            Publisher<StringMessage> publisher,
            StringMessage message,
            int messageCount,
            Action resetReceived,
            Func<int> receivedCount,
            string timeoutMessage)
        {
            resetReceived();
            long currentThreadAllocatedBefore = TryGetCurrentThreadAllocatedBytes();

            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < messageCount; i++)
            {
                publisher.PublishAsync(message).GetAwaiter().GetResult();
            }

            Assert.IsTrue(
                WaitUntil(() => receivedCount() >= messageCount, ReceiveTimeout),
                timeoutMessage);
            stopwatch.Stop();

            long currentThreadAllocatedAfter = TryGetCurrentThreadAllocatedBytes();
            Assert.AreEqual(messageCount, receivedCount());

            return new PublishBatchResult(
                messageCount,
                stopwatch.Elapsed,
                currentThreadAllocatedBefore,
                currentThreadAllocatedAfter);
        }

        private static void RecordThroughputSample(
            string groupPrefix,
            PublishBatchResult result,
            int serializedBytesPerMessage)
        {
            double elapsedMilliseconds = Math.Max(result.Elapsed.TotalMilliseconds, 0.001d);
            double elapsedSeconds = Math.Max(result.Elapsed.TotalSeconds, 0.000001d);
            double messagesPerSecond = result.MessageCount / elapsedSeconds;
            double serializedBytesPerSecond = (double)serializedBytesPerMessage * result.MessageCount / elapsedSeconds;

            Measure.Custom(new SampleGroup(groupPrefix + "elapsed_ms", SampleUnit.Millisecond, false), elapsedMilliseconds);
            Measure.Custom(new SampleGroup(groupPrefix + "mean_message_ms", SampleUnit.Millisecond, false), elapsedMilliseconds / result.MessageCount);
            Measure.Custom(new SampleGroup(groupPrefix + "messages_per_second", SampleUnit.Undefined, true), messagesPerSecond);
            Measure.Custom(new SampleGroup(groupPrefix + "serialized_bytes_per_second", SampleUnit.Undefined, true), serializedBytesPerSecond);
            Measure.Custom(new SampleGroup(groupPrefix + "serialized_bytes_per_message", SampleUnit.Byte, false), serializedBytesPerMessage);
            RecordAvailableDelta(
                groupPrefix + "current_thread_allocated_delta_bytes",
                result.CurrentThreadAllocatedBefore,
                result.CurrentThreadAllocatedAfter);
        }

        private static LoopbackParticipantPair CreateLoopbackPair()
        {
            var hub = new LoopbackHub();
            var multicastIp = IPAddress.Parse("239.255.0.1");
            var spdpLocator = Locator.FromUdpV4(multicastIp, 7400u);
            var userMulticastLocator = Locator.FromUdpV4(multicastIp, 7401u);
            int pairId = Interlocked.Increment(ref s_participantPairSequence);

            var writerSpdp = hub.Create(spdpLocator);
            var writerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.42.0.1"), 7482u));
            var writerUserMulticast = hub.Create(userMulticastLocator);
            var writerUserUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.42.0.1"), 7483u));
            var readerSpdp = hub.Create(spdpLocator);
            var readerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.42.0.2"), 7484u));
            var readerUserMulticast = hub.Create(userMulticastLocator);
            var readerUserUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.42.0.2"), 7485u));

            var writer = new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = 0,
                ParticipantId = 40 + pairId * 2,
                EntityName = "rclsharp_unity_writer_" + pairId,
                MulticastGroup = multicastIp,
                SpdpInterval = TimeSpan.FromMilliseconds(25),
                SedpInterval = TimeSpan.FromMilliseconds(25),
                CustomMulticastTransport = writerSpdp,
                CustomUnicastTransport = writerUnicast,
                CustomUserMulticastTransport = writerUserMulticast,
                CustomUserUnicastTransport = writerUserUnicast,
            });

            var reader = new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = 0,
                ParticipantId = 41 + pairId * 2,
                EntityName = "rclsharp_unity_reader_" + pairId,
                MulticastGroup = multicastIp,
                SpdpInterval = TimeSpan.FromMilliseconds(25),
                SedpInterval = TimeSpan.FromMilliseconds(25),
                CustomMulticastTransport = readerSpdp,
                CustomUnicastTransport = readerUnicast,
                CustomUserMulticastTransport = readerUserMulticast,
                CustomUserUnicastTransport = readerUserUnicast,
            });

            return new LoopbackParticipantPair(
                writer,
                reader,
                new[]
                {
                    writerSpdp,
                    writerUnicast,
                    writerUserMulticast,
                    writerUserUnicast,
                    readerSpdp,
                    readerUnicast,
                    readerUserMulticast,
                    readerUserUnicast,
                });
        }

        private static void AssertDiscovered(
            DomainParticipant writerParticipant,
            DomainParticipant readerParticipant,
            string ddsTopic)
        {
            bool discovered = WaitUntil(
                () => ReaderSawWriter(readerParticipant, writerParticipant, ddsTopic)
                   && WriterSawReader(writerParticipant, readerParticipant, ddsTopic),
                ReceiveTimeout);

            Assert.IsTrue(discovered, "SEDP discovery did not complete for topic " + ddsTopic + ".");
        }

        private static bool ReaderSawWriter(
            DomainParticipant readerParticipant,
            DomainParticipant writerParticipant,
            string ddsTopic)
        {
            return readerParticipant.DiscoveryDb.WriterSnapshot()
                .Any(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(writerParticipant.GuidPrefix));
        }

        private static bool WriterSawReader(
            DomainParticipant writerParticipant,
            DomainParticipant readerParticipant,
            string ddsTopic)
        {
            return writerParticipant.DiscoveryDb.ReaderSnapshot()
                .Any(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(readerParticipant.GuidPrefix));
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return condition();
        }

        private static string UniqueTopic(string prefix)
        {
            int id = Interlocked.Increment(ref s_topicSequence);
            return prefix + "_" + id;
        }

        private static StringMessage CreatePayloadMessage(string prefix, int payloadBytes)
        {
            string suffix = new string('x', Math.Max(1, payloadBytes - prefix.Length - 1));
            return new StringMessage(prefix + "_" + suffix);
        }

        private static void ForceFullCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static long PositiveDelta(long after, long before)
            => Math.Max(0L, after - before);

        private static long TryGetCurrentThreadAllocatedBytes()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method is null)
            {
                return -1L;
            }

            return (long)method.Invoke(null, null);
        }

        private static void RecordAvailableDelta(string name, long before, long after)
        {
            if (before < 0L || after < 0L)
            {
                return;
            }

            Measure.Custom(new SampleGroup(name, SampleUnit.Byte, false), PositiveDelta(after, before));
        }

        private readonly struct ThroughputScenario
        {
            public ThroughputScenario(int payloadBytes, int messageCount)
            {
                PayloadBytes = payloadBytes;
                MessageCount = messageCount;
            }

            public int PayloadBytes { get; }
            public int MessageCount { get; }
        }

        private readonly struct PublishBatchResult
        {
            public PublishBatchResult(
                int messageCount,
                TimeSpan elapsed,
                long currentThreadAllocatedBefore,
                long currentThreadAllocatedAfter)
            {
                MessageCount = messageCount;
                Elapsed = elapsed;
                CurrentThreadAllocatedBefore = currentThreadAllocatedBefore;
                CurrentThreadAllocatedAfter = currentThreadAllocatedAfter;
            }

            public int MessageCount { get; }
            public TimeSpan Elapsed { get; }
            public long CurrentThreadAllocatedBefore { get; }
            public long CurrentThreadAllocatedAfter { get; }
        }

        private sealed class LoopbackParticipantPair : IDisposable
        {
            private readonly LoopbackTransport[] _transports;

            public LoopbackParticipantPair(
                DomainParticipant writer,
                DomainParticipant reader,
                LoopbackTransport[] transports)
            {
                Writer = writer;
                Reader = reader;
                _transports = transports;
            }

            public DomainParticipant Writer { get; }
            public DomainParticipant Reader { get; }

            public void Start()
            {
                Writer.Start();
                Reader.Start();
            }

            public void Dispose()
            {
                try
                {
                    Writer.Dispose();
                }
                finally
                {
                    try
                    {
                        Reader.Dispose();
                    }
                    finally
                    {
                        for (int i = _transports.Length - 1; i >= 0; i--)
                        {
                            _transports[i].Dispose();
                        }
                    }
                }
            }
        }
    }
}
