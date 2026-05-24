using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Rtps;
using Rclsharp.Rtps.Submessages;

namespace Rclsharp.Tests.Rtps;

public class RtpsMessageTests
{
    private static readonly GuidPrefix SrcPrefix = new(
        new byte[] { 0x01, 0x0F, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x11, 0x22 });

    [Fact]
    public void Header_だけの_message_は_TryReadNext_が_false()
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new RtpsMessageWriter(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        w.BytesWritten.Should().Be(20);

        var r = new RtpsMessageReader(w.WrittenSpan);
        r.Version.Should().Be(ProtocolVersion.V2_4);
        r.VendorId.Should().Be(VendorId.EProsimaFastDds);
        r.SourceGuidPrefix.Should().Be(SrcPrefix);
        r.TryReadNext(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void InfoTimestamp_と_Heartbeat_を含む_message_の往復()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new RtpsMessageWriter(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);

        var ts = new InfoTimestampSubmessage(new Time(1234567890, 0u));
        var hb = new HeartbeatSubmessage(
            EntityId.Unknown, new EntityId(0x0000_0103u),
            new SequenceNumber(1L), new SequenceNumber(5L), count: 1, final: true);
        w.WriteInfoTimestamp(ts);
        w.WriteHeartbeat(hb);

        // 20 (header) + 4 (sub hdr) + 8 (ts body) + 4 (sub hdr) + 28 (hb body) = 64
        w.BytesWritten.Should().Be(64);

        var r = new RtpsMessageReader(w.WrittenSpan);

        // 1: InfoTimestamp
        r.TryReadNext(out var hdr1, out var body1).Should().BeTrue();
        hdr1.Kind.Should().Be(SubmessageKind.InfoTimestamp);
        hdr1.IsLittleEndian.Should().BeTrue();
        hdr1.Length.Should().Be((ushort)8);
        var read1 = InfoTimestampSubmessage.ReadBody(body1, hdr1.Endianness, hdr1.Flags);
        read1.Timestamp.Should().Be(ts.Timestamp);

        // 2: Heartbeat
        r.TryReadNext(out var hdr2, out var body2).Should().BeTrue();
        hdr2.Kind.Should().Be(SubmessageKind.Heartbeat);
        hdr2.Length.Should().Be((ushort)28);
        var read2 = HeartbeatSubmessage.ReadBody(body2, hdr2.Endianness, hdr2.Flags);
        read2.Final.Should().BeTrue();
        read2.LastSequenceNumber.Should().Be(new SequenceNumber(5L));

        // EOF
        r.TryReadNext(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void InfoTimestamp_invalidate_length_0_の後続_DATA_を読める()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new RtpsMessageWriter(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        var data = new DataSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(1L),
            serializedPayload: new byte[] { 0x00, 0x01, 0x00, 0x00, 0xAA });

        w.WriteInfoTimestamp(InfoTimestampSubmessage.CreateInvalidate());
        w.WriteData(data);

        var r = new RtpsMessageReader(w.WrittenSpan);
        r.TryReadNext(out var hdr1, out var body1).Should().BeTrue();
        hdr1.Kind.Should().Be(SubmessageKind.InfoTimestamp);
        hdr1.Length.Should().Be(0);
        body1.Length.Should().Be(0);
        InfoTimestampSubmessage.ReadBody(body1, hdr1.Endianness, hdr1.Flags).Invalidate.Should().BeTrue();

        r.TryReadNext(out var hdr2, out var body2).Should().BeTrue();
        hdr2.Kind.Should().Be(SubmessageKind.Data);
        DataSubmessage.ReadBody(body2, hdr2.Endianness, hdr2.Flags)
            .SerializedPayload.ToArray().Should().Equal(data.SerializedPayload.ToArray());
    }

    [Fact]
    public void Pad_length_0_の後続_DATA_を読める()
    {
        var data = new DataSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(2L),
            serializedPayload: new byte[] { 0x00, 0x01, 0x00, 0x00, 0xBB });
        var buf = new byte[RtpsHeader.Size + SubmessageHeader.Size + SubmessageHeader.Size + data.BodySize];
        RtpsHeader.Write(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        new SubmessageHeader(SubmessageKind.Pad, SubmessageFlags.Endianness, 0)
            .WriteTo(buf.AsSpan(RtpsHeader.Size, SubmessageHeader.Size));
        new SubmessageHeader(
                SubmessageKind.Data,
                (byte)(SubmessageFlags.Endianness | data.ExtraFlags),
                (ushort)data.BodySize)
            .WriteTo(buf.AsSpan(RtpsHeader.Size + SubmessageHeader.Size, SubmessageHeader.Size));
        data.WriteBody(
            buf.AsSpan(RtpsHeader.Size + SubmessageHeader.Size + SubmessageHeader.Size, data.BodySize),
            CdrEndianness.LittleEndian);

        var r = new RtpsMessageReader(buf);
        r.TryReadNext(out var hdr1, out var body1).Should().BeTrue();
        hdr1.Kind.Should().Be(SubmessageKind.Pad);
        body1.Length.Should().Be(0);

        r.TryReadNext(out var hdr2, out var body2).Should().BeTrue();
        hdr2.Kind.Should().Be(SubmessageKind.Data);
        DataSubmessage.ReadBody(body2, hdr2.Endianness, hdr2.Flags)
            .SerializedPayload.ToArray().Should().Equal(data.SerializedPayload.ToArray());
    }

    [Fact]
    public void Data_と_InfoDestination_を含む_message_の往復()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new RtpsMessageWriter(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);

        var dstPrefix = new GuidPrefix(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0xAA, 0xBB });
        var info = new InfoDestinationSubmessage(dstPrefix);
        var data = new DataSubmessage(
            EntityId.Unknown, new EntityId(0x0000_0103u),
            new SequenceNumber(42L),
            serializedPayload: new byte[] { 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, (byte)'h', (byte)'i', 0x00, 0x00 });

        w.WriteInfoDestination(info);
        w.WriteData(data);

        var r = new RtpsMessageReader(w.WrittenSpan);

        r.TryReadNext(out var hdr1, out var body1).Should().BeTrue();
        hdr1.Kind.Should().Be(SubmessageKind.InfoDestination);
        var info2 = InfoDestinationSubmessage.ReadBody(body1, hdr1.Endianness, hdr1.Flags);
        info2.GuidPrefix.Should().Be(dstPrefix);

        r.TryReadNext(out var hdr2, out var body2).Should().BeTrue();
        hdr2.Kind.Should().Be(SubmessageKind.Data);
        var data2 = DataSubmessage.ReadBody(body2, hdr2.Endianness, hdr2.Flags);
        data2.WriterSequenceNumber.Should().Be(new SequenceNumber(42L));
        data2.SerializedPayload.ToArray().Should().Equal(data.SerializedPayload.ToArray());
    }

    [Fact]
    public void DataFrag_を含む_message_の往復()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new RtpsMessageWriter(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        var frag = new DataFragSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(42L),
            fragmentStartingNumber: 2,
            fragmentsInSubmessage: 1,
            fragmentSize: 8,
            sampleSize: 24,
            serializedPayloadFragment: new byte[] { 8, 9, 10, 11, 12, 13, 14, 15 });

        w.WriteDataFrag(frag);

        var r = new RtpsMessageReader(w.WrittenSpan);
        r.TryReadNext(out var hdr, out var body).Should().BeTrue();
        hdr.Kind.Should().Be(SubmessageKind.DataFrag);

        var read = DataFragSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
        read.WriterSequenceNumber.Should().Be(new SequenceNumber(42L));
        read.FragmentStartingNumber.Should().Be(2);
        read.SerializedPayloadFragment.ToArray().Should().Equal(frag.SerializedPayloadFragment.ToArray());
    }

    [Fact]
    public void 不正な_submessage_length_で_InvalidDataException()
    {
        // 自前で偽の message を構築: header + submessage hdr (length=999, buffer は不足)
        var buf = new byte[20 + 4 + 8];
        RtpsHeader.Write(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        var fakeHdr = new SubmessageHeader(SubmessageKind.Data, SubmessageFlags.Endianness, 999);
        fakeHdr.WriteTo(buf.AsSpan(20, 4));

        bool threw = false;
        try
        {
            var r = new RtpsMessageReader(buf);
            r.TryReadNext(out _, out _);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        threw.Should().BeTrue();
    }

    [Fact]
    public void length_0_は_メッセージ末尾までを示す()
    {
        // 最終 submessage を length=0 にして、残りバイトすべてを body と扱う
        var buf = new byte[20 + 4 + 16];
        RtpsHeader.Write(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, SrcPrefix);
        var hdr = new SubmessageHeader(SubmessageKind.Data, SubmessageFlags.Endianness, 0); // length=0
        hdr.WriteTo(buf.AsSpan(20, 4));
        // body 16B (内容は何でもよい、本テストでは parse しない)

        var r = new RtpsMessageReader(buf);
        r.TryReadNext(out var h, out var body).Should().BeTrue();
        h.IsLengthExtendedToEnd.Should().BeTrue();
        body.Length.Should().Be(16);

        // 末尾以降は false
        r.TryReadNext(out _, out _).Should().BeFalse();
    }
}
