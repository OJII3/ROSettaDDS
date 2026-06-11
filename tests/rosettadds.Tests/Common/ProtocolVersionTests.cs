using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class ProtocolVersionTests
{
    [Fact]
    public void V2_4_は_2_4_を保持する()
    {
        ProtocolVersion.V2_4.Major.Should().Be(2);
        ProtocolVersion.V2_4.Minor.Should().Be(4);
    }

    [Fact]
    public void Current_は_V2_4_と一致する()
    {
        ProtocolVersion.Current.Should().Be(ProtocolVersion.V2_4);
    }

    [Fact]
    public void 等価演算子で同値判定できる()
    {
        var a = new ProtocolVersion(2, 4);
        var b = new ProtocolVersion(2, 4);
        var c = new ProtocolVersion(2, 3);

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.Equals((object)b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_は_メジャー_ドット_マイナー_形式()
    {
        new ProtocolVersion(2, 4).ToString().Should().Be("2.4");
    }
}

public class VendorIdTests
{
    [Fact]
    public void EProsimaFastDds_は_0x010F_を保持する()
    {
        VendorId.EProsimaFastDds.V0.Should().Be(0x01);
        VendorId.EProsimaFastDds.V1.Should().Be(0x0F);
    }

    [Fact]
    public void ROSettaDDS_既定値は_EProsimaFastDds_と等しい()
    {
        VendorId.ROSettaDDS.Should().Be(VendorId.EProsimaFastDds);
    }

    [Fact]
    public void Unknown_は_0_0()
    {
        VendorId.Unknown.V0.Should().Be(0);
        VendorId.Unknown.V1.Should().Be(0);
    }

    [Fact]
    public void ToString_は_16進_4桁()
    {
        new VendorId(0x01, 0x0F).ToString().Should().Be("0x010F");
    }
}
