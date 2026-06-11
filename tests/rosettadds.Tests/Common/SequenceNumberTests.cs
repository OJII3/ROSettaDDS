using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class SequenceNumberTests
{
    [Fact]
    public void Unknown_は_high_minus1_low_0()
    {
        SequenceNumber.Unknown.High.Should().Be(-1);
        SequenceNumber.Unknown.Low.Should().Be(0u);
        SequenceNumber.Unknown.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Zero_は_0_で_Unknown_ではない()
    {
        SequenceNumber.Zero.Value.Should().Be(0L);
        SequenceNumber.Zero.IsUnknown.Should().BeFalse();
    }

    [Fact]
    public void high_low_から_value_を組み立てる()
    {
        var sn = new SequenceNumber(0x1234, 0x5678ABCDu);
        sn.Value.Should().Be(0x0000_1234_5678_ABCDL);
        sn.High.Should().Be(0x1234);
        sn.Low.Should().Be(0x5678ABCDu);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteTo_と_Read_は_両エンディアンで往復する(bool littleEndian)
    {
        var src = new SequenceNumber(42L);
        var buf = new byte[8];
        src.WriteTo(buf, littleEndian);
        var roundtrip = SequenceNumber.Read(buf, littleEndian);
        roundtrip.Should().Be(src);
    }

    [Fact]
    public void 比較演算子()
    {
        var a = new SequenceNumber(10L);
        var b = new SequenceNumber(20L);

        var aDup = new SequenceNumber(10L);
        (a < b).Should().BeTrue();
        (b > a).Should().BeTrue();
        (a <= aDup).Should().BeTrue();
        (a >= aDup).Should().BeTrue();
        (a == aDup).Should().BeTrue();
        a.CompareTo(b).Should().BeNegative();
    }

    [Fact]
    public void 加減算演算子()
    {
        var a = new SequenceNumber(100L);
        (a + 5).Value.Should().Be(105L);
        (a - 5).Value.Should().Be(95L);
        (a - new SequenceNumber(40L)).Should().Be(60L);
    }
}
