using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class ParticipantRtpsReceiverAdapterTests
{
    private static readonly GuidPrefix LocalPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 1, 1);
    private static readonly GuidPrefix SourcePrefix = GuidPrefix.Create(VendorId.EProsimaFastDds, 2, 2, 2);

    [Fact]
    public void RegisterWriter_は_receiver_RegisterWriter_に委譲して例外を投げない()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var receiver = new ParticipantRtpsReceiver(prefix);
        var adapter = new ParticipantRtpsReceiverAdapter(receiver);
        var entityId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);
        var writer = MakeNullStatefulWriter(prefix, entityId);

        var act = () => adapter.RegisterWriter(entityId, writer);

        act.Should().NotThrow();
    }

    [Fact]
    public void RegisterReader_は_receiver_RegisterReader_に委譲して_DATA_が_handler_に届く()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var adapter = new ParticipantRtpsReceiverAdapter(receiver);
        var readerEntityId = new EntityId(1, EntityKind.UserDefinedReaderNoKey);
        var writerEntityId = new EntityId(2, EntityKind.UserDefinedWriterNoKey);
        var handler = new CapturingHandler();

        adapter.RegisterReader(readerEntityId, handler);
        receiver.OnPacketReceived(
            BuildDataPacket(SourcePrefix, readerEntityId, writerEntityId),
            AnySource);

        handler.DataReceived.Should().Be(1);
    }

    [Fact]
    public void UnregisterReader_は_receiver_UnregisterReader_に委譲して_DATA_が届かなくなる()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var adapter = new ParticipantRtpsReceiverAdapter(receiver);
        var readerEntityId = new EntityId(1, EntityKind.UserDefinedReaderNoKey);
        var writerEntityId = new EntityId(2, EntityKind.UserDefinedWriterNoKey);
        var handler = new CapturingHandler();

        adapter.RegisterReader(readerEntityId, handler);
        adapter.UnregisterReader(readerEntityId);
        receiver.OnPacketReceived(
            BuildDataPacket(SourcePrefix, readerEntityId, writerEntityId),
            AnySource);

        handler.DataReceived.Should().Be(0);
    }

    [Fact]
    public void UnregisterWriter_と_UnregisterReader_は例外を投げない()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var receiver = new ParticipantRtpsReceiver(prefix);
        var adapter = new ParticipantRtpsReceiverAdapter(receiver);
        var wId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);
        var rId = new EntityId(1, EntityKind.UserDefinedReaderNoKey);

        var act1 = () => adapter.UnregisterWriter(wId);
        var act2 = () => adapter.UnregisterReader(rId);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    [Fact]
    public void Constructor_は_null_receiverでArgumentNullExceptionを投げる()
    {
        var act = () => new ParticipantRtpsReceiverAdapter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static StatefulWriter MakeNullStatefulWriter(GuidPrefix prefix, EntityId entityId)
    {
        return new StatefulWriter(
            sendTransport: new NoopTransport(),
            multicastDestination: new Locator(LocatorKind.UdpV4, 0, stackalloc byte[16]),
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: prefix,
            writerEntityId: entityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(200),
            history: new WriterHistoryCache(new Guid(prefix, entityId), maxSamples: 1));
    }

    private static Locator AnySource => Locator.FromUdpV4(System.Net.IPAddress.Loopback, 1234u);

    private static byte[] BuildDataPacket(GuidPrefix sourcePrefix, EntityId readerId, EntityId writerId)
    {
        var buffer = new byte[256];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, sourcePrefix);
        writer.WriteData(new DataSubmessage(
            readerEntityId: readerId,
            writerEntityId: writerId,
            writerSn: new SequenceNumber(1),
            serializedPayload: new byte[] { 1, 2, 3, 4 },
            dataPresent: true));
        return writer.WrittenSpan.ToArray();
    }

    private sealed class NoopTransport : IRtpsTransport
    {
        public Locator LocalLocator => Locator.Invalid;
        public event Action<ReadOnlyMemory<byte>, Locator>? Received { add { } remove { } }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default) => default;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class NoopHandler : IRtpsSubmessageHandler
    {
        public void OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness) { }
        public void OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness) { }
        public void OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb) { }
        public void OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack) { }
        public void OnGap(in RtpsReceiverContext ctx, GapSubmessage gap) { }
    }

    private sealed class CapturingHandler : IRtpsSubmessageHandler
    {
        public int DataReceived { get; private set; }

        void IRtpsSubmessageHandler.OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness)
        {
            DataReceived++;
        }

        void IRtpsSubmessageHandler.OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness) { }
        void IRtpsSubmessageHandler.OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb) { }
        void IRtpsSubmessageHandler.OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack) { }
        void IRtpsSubmessageHandler.OnGap(in RtpsReceiverContext ctx, GapSubmessage gap) { }
    }
}
