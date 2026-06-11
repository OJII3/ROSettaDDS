using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.BuiltinInterfaces;

namespace ROSettaDDS.Tests.Msgs.BuiltinInterfaces;

/// <summary>
/// builtin_interfaces/Time, Duration の CDR シリアライズテスト。
/// </summary>
public class TimeDurationTests
{
    private static byte[] Serialize<T>(ICdrSerializer<T> serializer, in T value, CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[32];
        var w = new CdrWriter(buf, endian);
        serializer.Serialize(ref w, in value);
        return buf[..w.BytesWritten].ToArray();
    }

    private static T Deserialize<T>(ICdrSerializer<T> serializer, ReadOnlySpan<byte> bytes, CdrEndianness endian)
    {
        var r = new CdrReader(bytes, endian);
        serializer.Deserialize(ref r, out T value);
        return value;
    }

    [Fact]
    public void Time_LE_bit_exact()
    {
        var t = new Time(1, 2u);
        Serialize(TimeSerializer.Instance, in t, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x01, 0x00, 0x00, 0x00,   // sec = 1
                0x02, 0x00, 0x00, 0x00);  // nanosec = 2
    }

    [Fact]
    public void Time_BE_bit_exact()
    {
        var t = new Time(1, 2u);
        Serialize(TimeSerializer.Instance, in t, CdrEndianness.BigEndian)
            .Should().Equal(
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x02);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Time_roundtrip(CdrEndianness endian)
    {
        var t = new Time(-123, 999_999_999u);
        var bytes = Serialize(TimeSerializer.Instance, in t, endian);
        var read = Deserialize(TimeSerializer.Instance, bytes, endian);
        read.Sec.Should().Be(-123);
        read.Nanosec.Should().Be(999_999_999u);
    }

    [Fact]
    public void Duration_LE_bit_exact()
    {
        var d = new Duration(-1, 0xFFFFFFFFu);
        Serialize(DurationSerializer.Instance, in d, CdrEndianness.LittleEndian)
            .Should().Equal(
                0xFF, 0xFF, 0xFF, 0xFF,   // sec = -1
                0xFF, 0xFF, 0xFF, 0xFF);  // nanosec = 0xFFFFFFFF
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Duration_roundtrip(CdrEndianness endian)
    {
        var d = new Duration(42, 500_000_000u);
        var bytes = Serialize(DurationSerializer.Instance, in d, endian);
        var read = Deserialize(DurationSerializer.Instance, bytes, endian);
        read.Sec.Should().Be(42);
        read.Nanosec.Should().Be(500_000_000u);
    }

    [Fact]
    public void Time_サイズは8バイト()
    {
        TimeSerializer.Instance.GetSerializedSize(new Time(0, 0)).Should().Be(8);
    }

    [Fact]
    public void Duration_サイズは8バイト()
    {
        DurationSerializer.Instance.GetSerializedSize(new Duration(0, 0)).Should().Be(8);
    }
}
