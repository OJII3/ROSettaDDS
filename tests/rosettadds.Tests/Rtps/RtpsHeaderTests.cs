using ROSettaDDS.Common;
using ROSettaDDS.Rtps;

namespace ROSettaDDS.Tests.Rtps;

public class RtpsHeaderTests
{
    [Fact]
    public void Size_は_20()
    {
        RtpsHeader.Size.Should().Be(20);
    }

    [Fact]
    public void Write_と_Read_で往復する()
    {
        var prefix = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var buf = new byte[20];
        RtpsHeader.Write(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, prefix);

        var (v, id, p) = RtpsHeader.Read(buf);
        v.Should().Be(ProtocolVersion.V2_4);
        id.Should().Be(VendorId.EProsimaFastDds);
        p.Should().Be(prefix);
    }

    [Fact]
    public void bit_exact_先頭4Bは_RTPS_ASCII()
    {
        var prefix = new GuidPrefix(new byte[12]);
        var buf = new byte[20];
        RtpsHeader.Write(buf, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, prefix);

        // "RTPS" = 0x52, 0x54, 0x50, 0x53
        buf[0].Should().Be((byte)'R');
        buf[1].Should().Be((byte)'T');
        buf[2].Should().Be((byte)'P');
        buf[3].Should().Be((byte)'S');
        buf[4].Should().Be((byte)2);   // version major
        buf[5].Should().Be((byte)4);   // version minor
        buf[6].Should().Be((byte)0x01); // vendor v0
        buf[7].Should().Be((byte)0x0F); // vendor v1
    }

    [Fact]
    public void TryRead_は_magic_不一致で_false()
    {
        var buf = new byte[20];
        buf[0] = (byte)'X';
        buf[1] = (byte)'T';
        buf[2] = (byte)'P';
        buf[3] = (byte)'S';
        var ok = RtpsHeader.TryRead(buf, out _, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryRead_は_長さ不足で_false()
    {
        var buf = new byte[10];
        var ok = RtpsHeader.TryRead(buf, out _, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void Read_は_magic_不一致で_例外()
    {
        var buf = new byte[20];
        Action act = () => RtpsHeader.Read(buf); // 全 0
        act.Should().Throw<InvalidDataException>();
    }
}
