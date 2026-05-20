using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Rtps.Submessages;

namespace Rclsharp.Tests.Rtps;

public class InfoTimestampSubmessageTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void timestamp_の往復(CdrEndianness endian)
    {
        var src = new InfoTimestampSubmessage(new Time(1234567890, 0xDEAD_BEEFu));
        src.BodySize.Should().Be(8);
        src.ExtraFlags.Should().Be((byte)0);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, endian);

        var read = InfoTimestampSubmessage.ReadBody(buf, endian, flags: 0);
        read.Invalidate.Should().BeFalse();
        read.Timestamp.Should().Be(src.Timestamp);
    }

    [Fact]
    public void Invalidate_は_body_0B_かつ_I_フラグ()
    {
        var src = InfoTimestampSubmessage.CreateInvalidate();
        src.BodySize.Should().Be(0);
        src.ExtraFlags.Should().Be(SubmessageFlags.InfoTsInvalidate);

        var read = InfoTimestampSubmessage.ReadBody(ReadOnlySpan<byte>.Empty, CdrEndianness.LittleEndian, SubmessageFlags.InfoTsInvalidate);
        read.Invalidate.Should().BeTrue();
    }
}

public class InfoDestinationSubmessageTests
{
    [Fact]
    public void GuidPrefix_の往復()
    {
        var prefix = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var src = new InfoDestinationSubmessage(prefix);
        src.BodySize.Should().Be(12);

        var buf = new byte[12];
        src.WriteBody(buf, CdrEndianness.LittleEndian);
        buf.Should().Equal(prefix.ToByteArray());

        var read = InfoDestinationSubmessage.ReadBody(buf, CdrEndianness.LittleEndian, 0);
        read.GuidPrefix.Should().Be(prefix);
    }
}

public class HeartbeatSubmessageTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Heartbeat_の往復(CdrEndianness endian)
    {
        var src = new HeartbeatSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(1L),
            new SequenceNumber(42L),
            count: 7,
            final: true,
            liveliness: false);
        src.ExtraFlags.Should().Be(SubmessageFlags.HeartbeatFinal);

        var buf = new byte[HeartbeatSubmessage.BodySize];
        src.WriteBody(buf, endian);

        var read = HeartbeatSubmessage.ReadBody(buf, endian, src.ExtraFlags);
        read.ReaderEntityId.Should().Be(src.ReaderEntityId);
        read.WriterEntityId.Should().Be(src.WriterEntityId);
        read.FirstSequenceNumber.Should().Be(src.FirstSequenceNumber);
        read.LastSequenceNumber.Should().Be(src.LastSequenceNumber);
        read.Count.Should().Be(src.Count);
        read.Final.Should().BeTrue();
        read.Liveliness.Should().BeFalse();
    }

    [Fact]
    public void Heartbeat_LE_の_bit_exact()
    {
        var src = new HeartbeatSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(1L),
            new SequenceNumber(1L),
            count: 1);
        var buf = new byte[28];
        src.WriteBody(buf, CdrEndianness.LittleEndian);

        // readerId = 00 00 00 00 (Unknown)
        // writerId = 00 00 01 03
        // firstSN = (high=0, low=1) LE = 00 00 00 00 01 00 00 00
        // lastSN  = (high=0, low=1) LE = 00 00 00 00 01 00 00 00
        // count = 1 LE = 01 00 00 00
        buf.Should().Equal(
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x01, 0x03,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00);
    }
}

public class AckNackSubmessageTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void AckNack_の往復(CdrEndianness endian)
    {
        var snSet = new SequenceNumberSet(new SequenceNumber(10L), 32, new uint[] { 0x80000001u });
        var src = new AckNackSubmessage(
            new EntityId(0x0000_0107u),
            new EntityId(0x0000_0103u),
            snSet,
            count: 3,
            final: true);
        src.ExtraFlags.Should().Be(SubmessageFlags.AckNackFinal);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, endian);

        var read = AckNackSubmessage.ReadBody(buf, endian, src.ExtraFlags);
        read.ReaderEntityId.Should().Be(src.ReaderEntityId);
        read.WriterEntityId.Should().Be(src.WriterEntityId);
        read.Count.Should().Be(3);
        read.Final.Should().BeTrue();
        read.ReaderSnState.BitmapBase.Should().Be(snSet.BitmapBase);
        read.ReaderSnState.NumBits.Should().Be(32);
        read.ReaderSnState.Bitmap.Should().Equal(0x80000001u);
    }
}

public class GapSubmessageTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Gap_の往復(CdrEndianness endian)
    {
        var gapList = new SequenceNumberSet(new SequenceNumber(20L), 32, new uint[] { 0xC0000000u });
        var src = new GapSubmessage(
            new EntityId(0x0000_0107u),
            new EntityId(0x0000_0103u),
            new SequenceNumber(15L),
            gapList);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, endian);

        var read = GapSubmessage.ReadBody(buf, endian, flags: 0);
        read.GapStart.Should().Be(new SequenceNumber(15L));
        read.GapList.NumBits.Should().Be(32);
        read.GapList.Bitmap.Should().Equal(0xC0000000u);
    }
}

public class DataSubmessageTests
{
    [Fact]
    public void Data_payload_あり_InlineQos_なし()
    {
        var payload = new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF }; // 仮の CDR_LE encap + 4B
        var src = new DataSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(1L),
            payload);

        src.ExtraFlags.Should().Be(SubmessageFlags.DataData);
        src.BodySize.Should().Be(DataSubmessage.FixedHeaderSize + payload.Length);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, CdrEndianness.LittleEndian);

        var read = DataSubmessage.ReadBody(buf, CdrEndianness.LittleEndian, src.ExtraFlags);
        read.ReaderEntityId.Should().Be(src.ReaderEntityId);
        read.WriterEntityId.Should().Be(src.WriterEntityId);
        read.WriterSequenceNumber.Should().Be(src.WriterSequenceNumber);
        read.SerializedPayload.ToArray().Should().Equal(payload);
        read.InlineQos.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Data_InlineQos_あり_payload_あり()
    {
        // PL_CDR: PID 0x0070 (KEY_HASH) + length=4 + 4B value + SENTINEL (PID=0x01,len=0)
        var inlineQos = new byte[] {
            0x70, 0x00, 0x04, 0x00, 0xAA, 0xBB, 0xCC, 0xDD,
            0x01, 0x00, 0x00, 0x00 };
        var payload = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00 };

        var src = new DataSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(7L),
            serializedPayload: payload,
            inlineQos: inlineQos);
        src.InlineQosPresent.Should().BeTrue();
        src.ExtraFlags.Should().Be((byte)(SubmessageFlags.DataInlineQos | SubmessageFlags.DataData));

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, CdrEndianness.LittleEndian);

        var read = DataSubmessage.ReadBody(buf, CdrEndianness.LittleEndian, src.ExtraFlags);
        read.InlineQos.ToArray().Should().Equal(inlineQos);
        read.SerializedPayload.ToArray().Should().Equal(payload);
    }
}

public class DataFragSubmessageTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void DataFrag_payload_fragment_あり_InlineQos_なし(CdrEndianness endian)
    {
        var fragment = new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        var src = new DataFragSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(9L),
            fragmentStartingNumber: 1,
            fragmentsInSubmessage: 2,
            fragmentSize: 4,
            sampleSize: 12,
            serializedPayloadFragment: fragment);

        src.ExtraFlags.Should().Be(0);
        src.BodySize.Should().Be(DataFragSubmessage.FixedHeaderSize + fragment.Length);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, endian);

        var read = DataFragSubmessage.ReadBody(buf, endian, src.ExtraFlags);
        read.ReaderEntityId.Should().Be(src.ReaderEntityId);
        read.WriterEntityId.Should().Be(src.WriterEntityId);
        read.WriterSequenceNumber.Should().Be(src.WriterSequenceNumber);
        read.FragmentStartingNumber.Should().Be(1);
        read.FragmentsInSubmessage.Should().Be(2);
        read.FragmentSize.Should().Be(4);
        read.SampleSize.Should().Be(12);
        read.SerializedPayloadFragment.ToArray().Should().Equal(fragment);
        read.InlineQos.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void DataFrag_InlineQos_あり_payload_fragment_あり()
    {
        var inlineQos = new byte[] {
            0x70, 0x00, 0x04, 0x00, 0xAA, 0xBB, 0xCC, 0xDD,
            0x01, 0x00, 0x00, 0x00 };
        var fragment = new byte[] { 1, 2, 3, 4 };
        var src = new DataFragSubmessage(
            EntityId.Unknown,
            new EntityId(0x0000_0103u),
            new SequenceNumber(10L),
            fragmentStartingNumber: 3,
            fragmentsInSubmessage: 1,
            fragmentSize: 4,
            sampleSize: 16,
            serializedPayloadFragment: fragment,
            inlineQos: inlineQos);

        src.ExtraFlags.Should().Be(SubmessageFlags.DataInlineQos);

        var buf = new byte[src.BodySize];
        src.WriteBody(buf, CdrEndianness.LittleEndian);

        var read = DataFragSubmessage.ReadBody(buf, CdrEndianness.LittleEndian, src.ExtraFlags);
        read.InlineQos.ToArray().Should().Equal(inlineQos);
        read.SerializedPayloadFragment.ToArray().Should().Equal(fragment);
    }
}
