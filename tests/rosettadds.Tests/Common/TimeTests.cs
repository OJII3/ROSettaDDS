using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class TimeTests
{
    [Fact]
    public void Zero_と_Invalid_と_Infinite_の値を確認()
    {
        Time.Zero.Seconds.Should().Be(0);
        Time.Zero.Fraction.Should().Be(0u);

        Time.Invalid.Seconds.Should().Be(-1);
        Time.Invalid.Fraction.Should().Be(0xFFFF_FFFFu);

        Time.Infinite.Seconds.Should().Be(0x7FFF_FFFF);
        Time.Infinite.Fraction.Should().Be(0xFFFF_FFFFu);
    }

    [Fact]
    public void DateTime_往復で誤差が_1ms_未満()
    {
        var origin = new DateTime(2025, 1, 1, 12, 34, 56, 789, DateTimeKind.Utc);
        var t = Time.FromDateTime(origin);
        var roundtrip = t.ToDateTime();
        (roundtrip - origin).TotalMilliseconds.Should().BeApproximately(0, 1.0);
    }

    [Fact]
    public void Now_は_現在時刻に近い()
    {
        var before = DateTime.UtcNow;
        var t = Time.Now();
        var after = DateTime.UtcNow;

        t.ToDateTime().Should().BeOnOrAfter(before.AddSeconds(-1));
        t.ToDateTime().Should().BeOnOrBefore(after.AddSeconds(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteTo_と_Read_は_両エンディアンで往復する(bool littleEndian)
    {
        var src = new Time(1234567890, 0xDEAD_BEEFu);
        var buf = new byte[8];
        src.WriteTo(buf, littleEndian);
        var roundtrip = Time.Read(buf, littleEndian);
        roundtrip.Should().Be(src);
    }
}

public class DurationTests
{
    [Fact]
    public void Zero_と_Infinite_の値を確認()
    {
        Duration.Zero.Seconds.Should().Be(0);
        Duration.Zero.Fraction.Should().Be(0u);

        Duration.Infinite.Seconds.Should().Be(0x7FFF_FFFF);
        Duration.Infinite.Fraction.Should().Be(0xFFFF_FFFFu);
    }

    [Fact]
    public void TimeSpan_往復で誤差が_1ms_未満()
    {
        var origin = TimeSpan.FromMilliseconds(3500);
        var d = Duration.FromTimeSpan(origin);
        var roundtrip = d.ToTimeSpan();
        (roundtrip - origin).TotalMilliseconds.Should().BeApproximately(0, 1.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteTo_と_Read_は_両エンディアンで往復する(bool littleEndian)
    {
        var src = new Duration(42, 0x1234_5678u);
        var buf = new byte[8];
        src.WriteTo(buf, littleEndian);
        var roundtrip = Duration.Read(buf, littleEndian);
        roundtrip.Should().Be(src);
    }
}
