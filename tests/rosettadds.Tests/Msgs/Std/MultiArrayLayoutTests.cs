using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Msgs.Std;

/// <summary>
/// MultiArrayDimension / MultiArrayLayout の CDR シリアライズテスト。
/// </summary>
public class MultiArrayLayoutTests
{
    private static byte[] Serialize<T>(ICdrSerializer<T> serializer, in T value, CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[512];
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
    public void MultiArrayDimension_LE_bit_exact()
    {
        var dim = new MultiArrayDimension("x", 3, 3);
        Serialize(MultiArrayDimensionSerializer.Instance, in dim, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x02, 0x00, 0x00, 0x00,       // string length = 2 (incl. NUL)
                (byte)'x', 0x00,              // "x\0"
                0x00, 0x00,                   // padding to 4-byte for uint32
                0x03, 0x00, 0x00, 0x00,       // size = 3
                0x03, 0x00, 0x00, 0x00);      // stride = 3
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void MultiArrayDimension_roundtrip(CdrEndianness endian)
    {
        var dim = new MultiArrayDimension("rows", 42, 7);
        var bytes = Serialize(MultiArrayDimensionSerializer.Instance, in dim, endian);
        var read = Deserialize(MultiArrayDimensionSerializer.Instance, bytes, endian);
        read.Label.Should().Be("rows");
        read.Size.Should().Be(42u);
        read.Stride.Should().Be(7u);
    }

    [Fact]
    public void MultiArrayLayout_LE_bit_exact_空dim()
    {
        var layout = new MultiArrayLayout([], 0);
        Serialize(MultiArrayLayoutSerializer.Instance, in layout, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x00, 0x00, 0x00, 0x00,       // dim count = 0
                0x00, 0x00, 0x00, 0x00);      // data_offset = 0
    }

    [Fact]
    public void MultiArrayLayout_LE_bit_exact_dim_1要素()
    {
        var layout = new MultiArrayLayout(
            [new MultiArrayDimension("x", 3, 3)],
            0);
        Serialize(MultiArrayLayoutSerializer.Instance, in layout, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x01, 0x00, 0x00, 0x00,       // dim count = 1
                // MultiArrayDimension("x", 3, 3)
                0x02, 0x00, 0x00, 0x00,
                (byte)'x', 0x00, 0x00, 0x00,
                0x03, 0x00, 0x00, 0x00,
                0x03, 0x00, 0x00, 0x00,
                // data_offset
                0x00, 0x00, 0x00, 0x00);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void MultiArrayLayout_roundtrip(CdrEndianness endian)
    {
        var layout = new MultiArrayLayout(
            [
                new MultiArrayDimension("rows", 2, 6),
                new MultiArrayDimension("cols", 3, 3),
            ],
            1);
        var bytes = Serialize(MultiArrayLayoutSerializer.Instance, in layout, endian);
        var read = Deserialize(MultiArrayLayoutSerializer.Instance, bytes, endian);
        read.Dim.Should().HaveCount(2);
        read.Dim[0].Label.Should().Be("rows");
        read.Dim[0].Size.Should().Be(2u);
        read.Dim[0].Stride.Should().Be(6u);
        read.Dim[1].Label.Should().Be("cols");
        read.DataOffset.Should().Be(1u);
    }

    [Fact]
    public void MultiArrayLayout_dim_countが上限を超えたら拒否する()
    {
        Span<byte> buf = stackalloc byte[8];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteSequenceLength(3);
        byte[] bytes = buf[..w.BytesWritten].ToArray();

        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new CdrReader(
                bytes,
                CdrEndianness.LittleEndian,
                limits: new CdrReadLimits(maxSequenceElements: 2));
            MultiArrayLayoutSerializer.Instance.Deserialize(ref r, out _);
        });
    }
}
