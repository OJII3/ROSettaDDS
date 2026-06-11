using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Msgs.Std;

/// <summary>
/// std_msgs/ColorRGBA の CDR シリアライズテスト。
/// </summary>
public class ColorRgbaTests
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
    public void ColorRGBA_LE_bit_exact()
    {
        // r=1.0, g=0.0, b=0.0, a=1.0 — 1.0f = 0x3F800000 (IEEE 754)
        var c = new ColorRgba(1.0f, 0.0f, 0.0f, 1.0f);
        Serialize(ColorRgbaSerializer.Instance, in c, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x00, 0x00, 0x80, 0x3F,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x80, 0x3F);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void ColorRGBA_roundtrip(CdrEndianness endian)
    {
        var c = new ColorRgba(0.25f, 0.5f, 0.75f, 1.0f);
        var bytes = Serialize(ColorRgbaSerializer.Instance, in c, endian);
        var read = Deserialize(ColorRgbaSerializer.Instance, bytes, endian);
        read.R.Should().Be(0.25f);
        read.G.Should().Be(0.5f);
        read.B.Should().Be(0.75f);
        read.A.Should().Be(1.0f);
    }

    [Fact]
    public void ColorRGBA_サイズは16バイト()
    {
        ColorRgbaSerializer.Instance.GetSerializedSize(new ColorRgba(0, 0, 0, 0))
            .Should().Be(16);
    }
}
