using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using Unity.PerformanceTesting;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

using UnityTime = UnityEngine.Time;

namespace ROSettaDDS.UnitySoak.Tests
{
    public sealed class ROSettaDDSUnitySoakTests
    {
        private const float SoakDurationSeconds = 60f;
        private const float CycleDurationSeconds = 5f;
        private const double PublishIntervalSeconds = 1d / 50d;
        private const long ManagedLeakThresholdBytes = 8L * 1024L * 1024L;
        private const long UnityMonoLeakThresholdBytes = 64L * 1024L * 1024L;
        private static int s_cycleSequence;

        [UnityTest]
        [Performance]
        public IEnumerator publishとcreate_disposeを60秒継続できる()
        {
            var frameTimeGroup = new SampleGroup(
                "rosettadds.soak.frame_time_ms",
                SampleUnit.Millisecond,
                increaseIsBetter: false);
            double soakStart = UnityTime.realtimeSinceStartupAsDouble;
            int totalReceived = 0;
            long managedBaseline = 0L;
            long unityMonoBaseline = 0L;
            long maxManagedRetained = 0L;
            long maxUnityMonoRetained = 0L;
            int completedCycles = 0;

            while (UnityTime.realtimeSinceStartupAsDouble - soakStart < SoakDurationSeconds)
            {
                int received = 0;
                int published = 0;
                int cycleId = Interlocked.Increment(ref s_cycleSequence);
                string topic = "unity_soak_" + cycleId;
                var pair = CreatePair(cycleId);
                Subscription<StringMessage> subscription = null;
                Publisher<StringMessage> publisher = null;

                try
                {
                    subscription = pair.Reader.CreateSubscription<StringMessage>(
                        topic,
                        StringMessageSerializer.Instance,
                        (_, _) => Interlocked.Increment(ref received));
                    publisher = pair.Writer.CreatePublisher<StringMessage>(
                        topic,
                        StringMessageSerializer.Instance);
                    pair.Start();

                    yield return WaitUntil(
                        () => IsDiscovered(pair, "rt/" + topic),
                        TimeSpan.FromSeconds(5));
                    Assert.IsTrue(IsDiscovered(pair, "rt/" + topic), "Soak cycle discovery timed out.");

                    double cycleEnd = Math.Min(
                        soakStart + SoakDurationSeconds,
                        UnityTime.realtimeSinceStartupAsDouble + CycleDurationSeconds);
                    double nextPublish = UnityTime.realtimeSinceStartupAsDouble;

                    while (UnityTime.realtimeSinceStartupAsDouble < cycleEnd)
                    {
                        Measure.Custom(frameTimeGroup, UnityTime.unscaledDeltaTime * 1000d);
                        double now = UnityTime.realtimeSinceStartupAsDouble;
                        while (now >= nextPublish)
                        {
                            publisher.PublishAsync(new StringMessage("soak-" + published))
                                .GetAwaiter()
                                .GetResult();
                            published++;
                            nextPublish += PublishIntervalSeconds;
                        }
                        yield return null;
                    }

                    yield return WaitUntil(
                        () => Volatile.Read(ref received) >= published,
                        TimeSpan.FromSeconds(5));
                    Assert.AreEqual(published, Volatile.Read(ref received), "Soak cycle lost messages.");
                    totalReceived += received;
                }
                finally
                {
                    publisher?.Dispose();
                    subscription?.Dispose();
                    pair.Dispose();
                }

                ForceFullCollection();
                long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
                long unityMonoAfter = Profiler.GetMonoUsedSizeLong();
                if (completedCycles == 0)
                {
                    managedBaseline = managedAfter;
                    unityMonoBaseline = unityMonoAfter;
                }
                else
                {
                    maxManagedRetained = Math.Max(maxManagedRetained, PositiveDelta(managedAfter, managedBaseline));
                    maxUnityMonoRetained = Math.Max(maxUnityMonoRetained, PositiveDelta(unityMonoAfter, unityMonoBaseline));
                }
                completedCycles++;
                yield return null;
            }

            Measure.Custom(
                new SampleGroup("rosettadds.soak.managed_heap_retained_bytes", SampleUnit.Byte, false),
                maxManagedRetained);
            Measure.Custom(
                new SampleGroup("rosettadds.soak.unity_mono_used_retained_bytes", SampleUnit.Byte, false),
                maxUnityMonoRetained);
            Measure.Custom(
                new SampleGroup("rosettadds.soak.messages_received", SampleUnit.Undefined, true),
                totalReceived);

            Assert.GreaterOrEqual(completedCycles, 2);
            Assert.Greater(totalReceived, 0);
            Assert.LessOrEqual(maxManagedRetained, ManagedLeakThresholdBytes);
            Assert.LessOrEqual(maxUnityMonoRetained, UnityMonoLeakThresholdBytes);
        }

        private static SoakParticipantPair CreatePair(int cycleId)
        {
            var hub = new LoopbackHub();
            var multicastIp = IPAddress.Parse("239.255.0.1");
            var spdpLocator = Locator.FromUdpV4(multicastIp, 7400u);
            var userMulticastLocator = Locator.FromUdpV4(multicastIp, 7401u);
            var writerTransports = CreateTransports(hub, spdpLocator, userMulticastLocator, "10.44.0.1");
            var readerTransports = CreateTransports(hub, spdpLocator, userMulticastLocator, "10.44.0.2");
            var writer = CreateParticipant(20, "writer", cycleId, multicastIp, writerTransports);
            var reader = CreateParticipant(21, "reader", cycleId, multicastIp, readerTransports);

            return new SoakParticipantPair(
                writer,
                reader,
                writerTransports.Concat(readerTransports).ToArray());
        }

        private static LoopbackTransport[] CreateTransports(
            LoopbackHub hub,
            Locator spdpLocator,
            Locator userMulticastLocator,
            string unicastAddress)
        {
            var ip = IPAddress.Parse(unicastAddress);
            return new[]
            {
                hub.Create(spdpLocator),
                hub.Create(Locator.FromUdpV4(ip, 7482u)),
                hub.Create(userMulticastLocator),
                hub.Create(Locator.FromUdpV4(ip, 7483u)),
            };
        }

        private static DomainParticipant CreateParticipant(
            int participantId,
            string role,
            int cycleId,
            IPAddress multicastIp,
            LoopbackTransport[] transports)
        {
            return new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = 0,
                ParticipantId = participantId,
                EntityName = "rosettadds_unity_soak_" + role + "_" + cycleId,
                MulticastGroup = multicastIp,
                SpdpInterval = TimeSpan.FromMilliseconds(25),
                SedpInterval = TimeSpan.FromMilliseconds(25),
                CustomMulticastTransport = transports[0],
                CustomUnicastTransport = transports[1],
                CustomUserMulticastTransport = transports[2],
                CustomUserUnicastTransport = transports[3],
            });
        }

        private static bool IsDiscovered(SoakParticipantPair pair, string ddsTopic)
        {
            return pair.Reader.DiscoveryDb.WriterSnapshot()
                       .Any(ep => ep.Data.TopicName == ddsTopic
                               && ep.Data.ParticipantGuid.Prefix.Equals(pair.Writer.GuidPrefix))
                && pair.Writer.DiscoveryDb.ReaderSnapshot()
                       .Any(ep => ep.Data.TopicName == ddsTopic
                               && ep.Data.ParticipantGuid.Prefix.Equals(pair.Reader.GuidPrefix));
        }

        private static IEnumerator WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            double deadline = UnityTime.realtimeSinceStartupAsDouble + timeout.TotalSeconds;
            while (UnityTime.realtimeSinceStartupAsDouble < deadline && !condition())
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

        private sealed class SoakParticipantPair : IDisposable
        {
            private readonly LoopbackTransport[] _transports;

            internal SoakParticipantPair(
                DomainParticipant writer,
                DomainParticipant reader,
                LoopbackTransport[] transports)
            {
                Writer = writer;
                Reader = reader;
                _transports = transports;
            }

            internal DomainParticipant Writer { get; }
            internal DomainParticipant Reader { get; }

            internal void Start()
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
