using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class ParticipantRtpsReceiverTests
{
    private static readonly GuidPrefix LocalPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 1, 1);
    private static readonly GuidPrefix SourcePrefix = GuidPrefix.Create(VendorId.EProsimaFastDds, 2, 2, 2);
    private static readonly EntityId ReaderA = new(0x000100u, EntityKind.UserDefinedReaderNoKey);
    private static readonly EntityId ReaderB = new(0x000200u, EntityKind.UserDefinedReaderNoKey);
    private static readonly EntityId WriterW = new(0x000300u, EntityKind.UserDefinedWriterNoKey);

    [Fact]
    public void 具体的な_reader_id_の_DATA_は対象_reader_だけに届く()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        var b = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);
        receiver.RegisterReader(ReaderB, b);

        receiver.OnPacketReceived(BuildDataPacket(SourcePrefix, ReaderA, WriterW), AnySource);

        a.DataReceived.Should().Be(1);
        b.DataReceived.Should().Be(0);
    }

    [Fact]
    public void UNKNOWN_reader_の_DATA_は全_reader_へブロードキャストされる()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        var b = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);
        receiver.RegisterReader(ReaderB, b);

        receiver.OnPacketReceived(BuildDataPacket(SourcePrefix, EntityId.Unknown, WriterW), AnySource);

        a.DataReceived.Should().Be(1);
        b.DataReceived.Should().Be(1);
    }

    [Fact]
    public void ACKNACK_は_writer_id_で対象_writer_へ届く()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var reader = new CapturingHandler();
        var writer = new CapturingHandler();
        receiver.RegisterReader(ReaderA, reader);
        receiver.RegisterWriter(WriterW, writer);

        receiver.OnPacketReceived(BuildAckNackPacket(SourcePrefix, ReaderA, WriterW), AnySource);

        writer.AckNackReceived.Should().Be(1);
        reader.AckNackReceived.Should().Be(0);
    }

    [Fact]
    public void INFO_DST_が他_participant_宛なら_submessage_を破棄する()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);

        var otherPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9);
        receiver.OnPacketReceived(BuildDataPacket(SourcePrefix, ReaderA, WriterW, destPrefix: otherPrefix), AnySource);

        a.DataReceived.Should().Be(0);
    }

    [Fact]
    public void INFO_DST_が自_participant_宛なら_submessage_を配送する()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);

        receiver.OnPacketReceived(BuildDataPacket(SourcePrefix, ReaderA, WriterW, destPrefix: LocalPrefix), AnySource);

        a.DataReceived.Should().Be(1);
    }

    [Fact]
    public void INFO_SRC_は後続_submessage_の送信元_prefix_を上書きする()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);

        var overriddenSource = GuidPrefix.Create(VendorId.EclipseCycloneDds, 7, 7, 7);
        receiver.OnPacketReceived(
            BuildDataPacket(SourcePrefix, ReaderA, WriterW, infoSrcPrefix: overriddenSource),
            AnySource);

        a.DataReceived.Should().Be(1);
        a.LastSource.Should().Be(overriddenSource);
    }

    [Fact]
    public void Unregister後の_reader_には届かない()
    {
        var receiver = new ParticipantRtpsReceiver(LocalPrefix);
        var a = new CapturingHandler();
        receiver.RegisterReader(ReaderA, a);
        receiver.UnregisterReader(ReaderA);

        receiver.OnPacketReceived(BuildDataPacket(SourcePrefix, EntityId.Unknown, WriterW), AnySource);

        a.DataReceived.Should().Be(0);
    }

    private static Locator AnySource => Locator.FromUdpV4(System.Net.IPAddress.Loopback, 1234u);

    private static byte[] BuildDataPacket(
        GuidPrefix sourcePrefix,
        EntityId readerId,
        EntityId writerId,
        GuidPrefix? destPrefix = null,
        GuidPrefix? infoSrcPrefix = null)
    {
        var buffer = new byte[256];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, sourcePrefix);
        if (infoSrcPrefix is { } src)
        {
            writer.WriteInfoSource(new InfoSourceSubmessage(ProtocolVersion.V2_4, VendorId.ROSettaDDS, src));
        }
        if (destPrefix is { } dst)
        {
            writer.WriteInfoDestination(new InfoDestinationSubmessage(dst));
        }
        writer.WriteData(new DataSubmessage(
            readerEntityId: readerId,
            writerEntityId: writerId,
            writerSn: new SequenceNumber(1),
            serializedPayload: new byte[] { 1, 2, 3, 4 },
            dataPresent: true));
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildAckNackPacket(GuidPrefix sourcePrefix, EntityId readerId, EntityId writerId)
    {
        var buffer = new byte[256];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, sourcePrefix);
        writer.WriteAckNack(new AckNackSubmessage(
            readerEntityId: readerId,
            writerEntityId: writerId,
            readerSnState: new SequenceNumberSet(new SequenceNumber(1), 0, Array.Empty<uint>()),
            count: 1,
            final: true));
        return writer.WrittenSpan.ToArray();
    }

    private sealed class CapturingHandler : IRtpsSubmessageHandler
    {
        public int DataReceived { get; private set; }
        public int AckNackReceived { get; private set; }
        public GuidPrefix LastSource { get; private set; }

        void IRtpsSubmessageHandler.OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness)
        {
            DataReceived++;
            LastSource = ctx.SourceGuidPrefix;
        }

        void IRtpsSubmessageHandler.OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness) { }
        void IRtpsSubmessageHandler.OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb) { }
        void IRtpsSubmessageHandler.OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack) => AckNackReceived++;
        void IRtpsSubmessageHandler.OnGap(in RtpsReceiverContext ctx, GapSubmessage gap) { }
    }
}
