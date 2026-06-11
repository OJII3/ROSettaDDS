using ROSettaDDS.Cdr;
using ROSettaDDS.Rtps.Submessages;

namespace ROSettaDDS.Tests.Rtps;

public class SubmessageHeaderTests
{
    [Fact]
    public void Size_は_4()
    {
        SubmessageHeader.Size.Should().Be(4);
    }

    [Fact]
    public void Endianness_フラグで_LE_BE_が判定される()
    {
        var le = new SubmessageHeader(SubmessageKind.Heartbeat, 0x01, 28);
        var be = new SubmessageHeader(SubmessageKind.Heartbeat, 0x00, 28);
        le.IsLittleEndian.Should().BeTrue();
        le.Endianness.Should().Be(CdrEndianness.LittleEndian);
        be.IsLittleEndian.Should().BeFalse();
        be.Endianness.Should().Be(CdrEndianness.BigEndian);
    }

    [Fact]
    public void IsLengthExtendedToEnd_は_length_0_で_true()
    {
        new SubmessageHeader(SubmessageKind.Data, 0x01, 0).IsLengthExtendedToEnd.Should().BeTrue();
        new SubmessageHeader(SubmessageKind.Data, 0x01, 1).IsLengthExtendedToEnd.Should().BeFalse();
    }

    [Fact]
    public void Write_と_Read_の往復_LE()
    {
        var src = new SubmessageHeader(SubmessageKind.Heartbeat, 0x03, 28);
        var buf = new byte[4];
        src.WriteTo(buf);

        // kind=0x07, flags=0x03, length=28 in LE = 0x1C, 0x00
        buf.Should().Equal(0x07, 0x03, 0x1C, 0x00);

        var read = SubmessageHeader.Read(buf);
        read.Kind.Should().Be(SubmessageKind.Heartbeat);
        read.Flags.Should().Be((byte)0x03);
        read.Length.Should().Be((ushort)28);
    }

    [Fact]
    public void Write_と_Read_の往復_BE()
    {
        var src = new SubmessageHeader(SubmessageKind.Data, 0x04, 256);
        var buf = new byte[4];
        src.WriteTo(buf);

        // kind=0x15, flags=0x04 (E=0 ≡ BE), length=256 in BE = 0x01, 0x00
        buf.Should().Equal(0x15, 0x04, 0x01, 0x00);

        var read = SubmessageHeader.Read(buf);
        read.Kind.Should().Be(SubmessageKind.Data);
        read.Length.Should().Be((ushort)256);
    }
}
