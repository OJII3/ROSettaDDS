using System.Net;
using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class LocatorTests
{
    [Fact]
    public void Size_は_24_AddressSize_は_16()
    {
        Locator.Size.Should().Be(24);
        Locator.AddressSize.Should().Be(16);
    }

    [Fact]
    public void FromUdpV4_は_先頭12B_をゼロパディングし末尾4B_に_IPv4_を格納する()
    {
        var loc = Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7400u);
        loc.Kind.Should().Be(LocatorKind.UdpV4);
        loc.Port.Should().Be(7400u);

        var addr = loc.AddressBytes();
        addr.Length.Should().Be(16);
        addr.Take(12).Should().AllSatisfy(b => b.Should().Be(0));
        addr.Skip(12).Should().Equal(192, 168, 1, 10);
    }

    [Fact]
    public void FromUdpV4_に_IPv6_を渡すと例外()
    {
        Action act = () => Locator.FromUdpV4(IPAddress.IPv6Loopback, 7400u);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromUdpV6_は_16B_を_アドレスとして格納する()
    {
        var ip = IPAddress.Parse("fe80::1");
        var loc = Locator.FromUdpV6(ip, 7400u);
        loc.Kind.Should().Be(LocatorKind.UdpV6);
        loc.AddressBytes().Should().Equal(ip.GetAddressBytes());
    }

    [Fact]
    public void ToIPAddress_で復元できる()
    {
        var loc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7400u);
        loc.ToIPAddress().Should().Be(IPAddress.Parse("10.0.0.1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteTo_と_Read_で往復する(bool littleEndian)
    {
        var src = Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7400u);
        var buf = new byte[24];
        src.WriteTo(buf, littleEndian);
        var roundtrip = Locator.Read(buf, littleEndian);
        roundtrip.Should().Be(src);
    }

    [Fact]
    public void Invalid_は_kind_minus1_port_0_address_全0()
    {
        var inv = Locator.Invalid;
        inv.Kind.Should().Be(LocatorKind.Invalid);
        inv.Port.Should().Be(0u);
        inv.AddressBytes().Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void 同値の_Locator_は_等価()
    {
        var a = Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7400u);
        var b = Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7400u);
        var c = Locator.FromUdpV4(IPAddress.Parse("192.168.1.11"), 7400u);

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_は_UDPv4_形式()
    {
        var loc = Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7400u);
        loc.ToString().Should().Be("UDPv4://192.168.1.10:7400");
    }
}
