using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.BuiltinInterfaces;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;
using BuiltinDuration = ROSettaDDS.Msgs.BuiltinInterfaces.Duration;
using BuiltinTime = ROSettaDDS.Msgs.BuiltinInterfaces.Time;

namespace ROSettaDDS.UnityPlayer.Tests
{
    public sealed class ROSettaDDSUnityAotPlayerTests
    {
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private static readonly MultiArrayLayout SampleLayout = new MultiArrayLayout(
            new[] { new MultiArrayDimension("axis", 3u, 3u) },
            1u);

        private static int s_topicSequence;

        [Test]
        public void 全生成msg型をAOT_Playerでpublish_receiveできる()
        {
            using var pair = PlayerLoopbackPair.Create();
            pair.Start();

            AssertRoundTrip(pair, DurationSerializer.Instance, new BuiltinDuration(-2, 345u));
            AssertRoundTrip(pair, TimeSerializer.Instance, new BuiltinTime(123, 456u));
            AssertRoundTrip(pair, BoolMessageSerializer.Instance, new BoolMessage(true));
            AssertRoundTrip(pair, ByteMessageSerializer.Instance, new ByteMessage(0xa5));
            AssertRoundTrip(pair, ByteMultiArraySerializer.Instance, new ByteMultiArray(SampleLayout, new byte[] { 0, 127, 255 }));
            AssertRoundTrip(pair, CharMessageSerializer.Instance, new CharMessage(-42));
            AssertRoundTrip(pair, ColorRgbaSerializer.Instance, new ColorRgba(0.1f, 0.2f, 0.3f, 0.4f));
            AssertRoundTrip(pair, EmptyMessageSerializer.Instance, new EmptyMessage());
            AssertRoundTrip(pair, Float32MessageSerializer.Instance, new Float32Message(12.5f));
            AssertRoundTrip(pair, Float32MultiArraySerializer.Instance, new Float32MultiArray(SampleLayout, new[] { -1.5f, 0f, 2.5f }));
            AssertRoundTrip(pair, Float64MessageSerializer.Instance, new Float64Message(-123.75d));
            AssertRoundTrip(pair, Float64MultiArraySerializer.Instance, new Float64MultiArray(SampleLayout, new[] { -1.5d, 0d, 2.5d }));
            AssertRoundTrip(pair, HeaderSerializer.Instance, new Header(new BuiltinTime(321, 654u), "map"));
            AssertRoundTrip(pair, Int8MessageSerializer.Instance, new Int8Message(-8));
            AssertRoundTrip(pair, Int8MultiArraySerializer.Instance, new Int8MultiArray(SampleLayout, new sbyte[] { -8, 0, 8 }));
            AssertRoundTrip(pair, Int16MessageSerializer.Instance, new Int16Message(-1600));
            AssertRoundTrip(pair, Int16MultiArraySerializer.Instance, new Int16MultiArray(SampleLayout, new short[] { -16, 0, 16 }));
            AssertRoundTrip(pair, Int32MessageSerializer.Instance, new Int32Message(-320000));
            AssertRoundTrip(pair, Int32MultiArraySerializer.Instance, new Int32MultiArray(SampleLayout, new[] { -32, 0, 32 }));
            AssertRoundTrip(pair, Int64MessageSerializer.Instance, new Int64Message(-6400000000L));
            AssertRoundTrip(pair, Int64MultiArraySerializer.Instance, new Int64MultiArray(SampleLayout, new[] { -64L, 0L, 64L }));
            AssertRoundTrip(pair, MultiArrayDimensionSerializer.Instance, new MultiArrayDimension("width", 640u, 1920u));
            AssertRoundTrip(pair, MultiArrayLayoutSerializer.Instance, SampleLayout);
            AssertRoundTrip(pair, StringMessageSerializer.Instance, new StringMessage("IL2CPPで日本語を往復"));
            AssertRoundTrip(pair, UInt8MessageSerializer.Instance, new UInt8Message(250));
            AssertRoundTrip(pair, UInt8MultiArraySerializer.Instance, new UInt8MultiArray(SampleLayout, new byte[] { 1, 2, 250 }));
            AssertRoundTrip(pair, UInt16MessageSerializer.Instance, new UInt16Message(65000));
            AssertRoundTrip(pair, UInt16MultiArraySerializer.Instance, new UInt16MultiArray(SampleLayout, new ushort[] { 1, 2, 65000 }));
            AssertRoundTrip(pair, UInt32MessageSerializer.Instance, new UInt32Message(4000000000u));
            AssertRoundTrip(pair, UInt32MultiArraySerializer.Instance, new UInt32MultiArray(SampleLayout, new[] { 1u, 2u, 4000000000u }));
            AssertRoundTrip(pair, UInt64MessageSerializer.Instance, new UInt64Message(18000000000000000000ul));
            AssertRoundTrip(pair, UInt64MultiArraySerializer.Instance, new UInt64MultiArray(SampleLayout, new[] { 1ul, 2ul, 18000000000000000000ul }));
        }

        private static void AssertRoundTrip<T>(
            PlayerLoopbackPair pair,
            ICdrSerializer<T> serializer,
            T value)
        {
            string topic = "unity_player_aot_" + typeof(T).Name + "_" + Interlocked.Increment(ref s_topicSequence);
            T received = default;
            int receivedCount = 0;

            using var sub = pair.Reader.CreateSubscription<T>(
                topic,
                serializer,
                message =>
                {
                    received = message;
                    Interlocked.Exchange(ref receivedCount, 1);
                });
            using var pub = pair.Writer.CreatePublisher<T>(topic, serializer);

            pair.AssertDiscovered(topic);
            var expectedPayload = pub.SerializeWithEncapsulation(value).ToArray();
            pub.PublishAsync(value).GetAwaiter().GetResult();

            Assert.IsTrue(
                WaitUntil(() => Volatile.Read(ref receivedCount) == 1),
                "Message roundtrip timed out for " + typeof(T).FullName + ".");

            CollectionAssert.AreEqual(
                expectedPayload,
                pub.SerializeWithEncapsulation(received).ToArray(),
                "CDR payload mismatch for " + typeof(T).FullName + ".");
        }

        private static bool WaitUntil(Func<bool> condition)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < ReceiveTimeout)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return condition();
        }

        private sealed class PlayerLoopbackPair : IDisposable
        {
            private readonly IRtpsTransport[] _transports;

            private PlayerLoopbackPair(
                DomainParticipant writer,
                DomainParticipant reader,
                IRtpsTransport[] transports)
            {
                Writer = writer;
                Reader = reader;
                _transports = transports;
            }

            internal DomainParticipant Writer { get; }
            internal DomainParticipant Reader { get; }

            internal static PlayerLoopbackPair Create()
            {
                var hub = new LoopbackHub();
                var multicastIp = IPAddress.Parse("239.255.0.1");
                var spdpLocator = Locator.FromUdpV4(multicastIp, 7400u);
                var userMulticastLocator = Locator.FromUdpV4(multicastIp, 7401u);
                var writerSpdp = hub.Create(spdpLocator);
                var writerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.44.0.1"), 7640u));
                var writerUserMulticast = hub.Create(userMulticastLocator);
                var writerUserUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.44.0.1"), 7641u));
                var readerSpdp = hub.Create(spdpLocator);
                var readerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.44.0.2"), 7642u));
                var readerUserMulticast = hub.Create(userMulticastLocator);
                var readerUserUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.44.0.2"), 7643u));
                var transports = new IRtpsTransport[]
                {
                    writerSpdp,
                    writerUnicast,
                    writerUserMulticast,
                    writerUserUnicast,
                    readerSpdp,
                    readerUnicast,
                    readerUserMulticast,
                    readerUserUnicast,
                };

                return new PlayerLoopbackPair(
                    CreateParticipant(112, "writer", multicastIp, writerSpdp, writerUnicast, writerUserMulticast, writerUserUnicast),
                    CreateParticipant(113, "reader", multicastIp, readerSpdp, readerUnicast, readerUserMulticast, readerUserUnicast),
                    transports);
            }

            internal void Start()
            {
                Writer.Start();
                Reader.Start();
            }

            internal void AssertDiscovered(string topic)
            {
                string ddsTopic = "rt/" + topic;
                Assert.IsTrue(
                    WaitUntil(
                        () => Reader.DiscoveryDb.WriterSnapshot()
                                  .Any(ep => ep.Data.TopicName == ddsTopic
                                          && ep.Data.ParticipantGuid.Prefix.Equals(Writer.GuidPrefix))
                           && Writer.DiscoveryDb.ReaderSnapshot()
                                  .Any(ep => ep.Data.TopicName == ddsTopic
                                          && ep.Data.ParticipantGuid.Prefix.Equals(Reader.GuidPrefix))),
                    "SEDP discovery did not complete for topic " + ddsTopic + ".");
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

            private static DomainParticipant CreateParticipant(
                int participantId,
                string role,
                IPAddress multicastIp,
                IRtpsTransport spdp,
                IRtpsTransport unicast,
                IRtpsTransport userMulticast,
                IRtpsTransport userUnicast)
            {
                return new DomainParticipant(new DomainParticipantOptions
                {
                    DomainId = 0,
                    ParticipantId = participantId,
                    EntityName = "rosettadds_unity_player_" + role,
                    MulticastGroup = multicastIp,
                    SpdpInterval = TimeSpan.FromMilliseconds(25),
                    SedpInterval = TimeSpan.FromMilliseconds(25),
                    UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(25),
                    CustomMulticastTransport = spdp,
                    CustomUnicastTransport = unicast,
                    CustomUserMulticastTransport = userMulticast,
                    CustomUserUnicastTransport = userUnicast,
                });
            }
        }
    }
}
