using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

namespace ROSettaDDS.UnityVerification.Tests
{
    internal static class UnityLoopbackTestSupport
    {
        internal static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private const int ParticipantPairFirstId = 100;
        private const int ParticipantIdsPerPair = 2;
        private const int ParticipantPairSlotCount =
            (RtpsConstants.MaxParticipantId - ParticipantPairFirstId + 1) / ParticipantIdsPerPair;

        private static int s_topicSequence;
        private static int s_participantPairSequence;

        internal static LoopbackParticipantPair CreatePair(
            Func<LoopbackTransport, IRtpsTransport> decorateWriterUserUnicast = null)
        {
            var hub = new LoopbackHub();
            var multicastIp = IPAddress.Parse("239.255.0.1");
            var spdpLocator = Locator.FromUdpV4(multicastIp, 7400u);
            var userMulticastLocator = Locator.FromUdpV4(multicastIp, 7401u);
            int pairId = Interlocked.Increment(ref s_participantPairSequence);
            int pairSlot = (int)((uint)(pairId - 1) % ParticipantPairSlotCount);
            int writerParticipantId = ParticipantPairFirstId + pairSlot * ParticipantIdsPerPair;
            int readerParticipantId = writerParticipantId + 1;

            var writerSpdp = hub.Create(spdpLocator);
            var writerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.43.0.1"), 7582u));
            var writerUserMulticast = hub.Create(userMulticastLocator);
            var writerUserUnicastInner = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.43.0.1"), 7583u));
            IRtpsTransport writerUserUnicast = decorateWriterUserUnicast is null
                ? writerUserUnicastInner
                : decorateWriterUserUnicast(writerUserUnicastInner);
            var readerSpdp = hub.Create(spdpLocator);
            var readerUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.43.0.2"), 7584u));
            var readerUserMulticast = hub.Create(userMulticastLocator);
            var readerUserUnicast = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.43.0.2"), 7585u));

            var writer = CreateParticipant(
                writerParticipantId,
                "writer",
                multicastIp,
                writerSpdp,
                writerUnicast,
                writerUserMulticast,
                writerUserUnicast);
            var reader = CreateParticipant(
                readerParticipantId,
                "reader",
                multicastIp,
                readerSpdp,
                readerUnicast,
                readerUserMulticast,
                readerUserUnicast);

            return new LoopbackParticipantPair(
                writer,
                reader,
                new IRtpsTransport[]
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

        internal static void AssertDiscovered(LoopbackParticipantPair pair, string topic)
        {
            string ddsTopic = "rt/" + topic;
            bool discovered = WaitUntil(
                () => pair.Reader.DiscoveryDb.WriterSnapshot()
                          .Any(ep => ep.Data.TopicName == ddsTopic
                                  && ep.Data.ParticipantGuid.Prefix.Equals(pair.Writer.GuidPrefix))
                   && pair.Writer.DiscoveryDb.ReaderSnapshot()
                          .Any(ep => ep.Data.TopicName == ddsTopic
                                  && ep.Data.ParticipantGuid.Prefix.Equals(pair.Reader.GuidPrefix)),
                ReceiveTimeout);

            Assert.IsTrue(discovered, "SEDP discovery did not complete for topic " + ddsTopic + ".");
        }

        internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < timeout)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return condition();
        }

        internal static string UniqueTopic(string prefix)
            => prefix + "_" + Interlocked.Increment(ref s_topicSequence);

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
                EntityName = "rosettadds_unity_phase2_" + role + "_" + participantId,
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

    internal sealed class LoopbackParticipantPair : IDisposable
    {
        private readonly IRtpsTransport[] _transports;

        internal LoopbackParticipantPair(
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

    internal sealed class LossyTransport : IRtpsTransport
    {
        private readonly IRtpsTransport _inner;
        private readonly Func<ReadOnlyMemory<byte>, Locator, bool> _shouldDrop;
        private int _remainingDrops;
        private int _droppedCount;

        internal LossyTransport(
            IRtpsTransport inner,
            int dropCount,
            Func<ReadOnlyMemory<byte>, Locator, bool> shouldDrop)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _shouldDrop = shouldDrop ?? throw new ArgumentNullException(nameof(shouldDrop));
            _remainingDrops = dropCount >= 0
                ? dropCount
                : throw new ArgumentOutOfRangeException(nameof(dropCount));
        }

        public Locator LocalLocator => _inner.LocalLocator;
        internal int DroppedCount => Volatile.Read(ref _droppedCount);

        public event Action<ReadOnlyMemory<byte>, Locator> Received
        {
            add => _inner.Received += value;
            remove => _inner.Received -= value;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> packet,
            Locator destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _remainingDrops) > 0
                && _shouldDrop(packet, destination)
                && Interlocked.Decrement(ref _remainingDrops) >= 0)
            {
                Interlocked.Increment(ref _droppedCount);
                return default;
            }

            return _inner.SendAsync(packet, destination, cancellationToken);
        }

        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Dispose() => _inner.Dispose();
    }

    internal static class RtpsPacketPredicates
    {
        internal static bool ContainsUserData(ReadOnlyMemory<byte> packet, Locator destination)
        {
            var span = packet.Span;
            if (!RtpsHeader.TryRead(span, out _, out _, out _))
            {
                return false;
            }

            int offset = RtpsHeader.Size;
            while (offset + SubmessageHeader.Size <= span.Length)
            {
                var header = SubmessageHeader.Read(span.Slice(offset, SubmessageHeader.Size));
                if (header.Kind == SubmessageKind.Data || header.Kind == SubmessageKind.DataFrag)
                {
                    return true;
                }

                offset += SubmessageHeader.Size;
                if (header.IsLengthExtendedToEnd)
                {
                    return false;
                }
                offset += header.Length;
            }
            return false;
        }
    }
}
