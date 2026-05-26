using Rclsharp.Cdr;
using Rclsharp.Msgs.Std;

namespace Rclsharp.Tests.Msgs.Std;

/// <summary>
/// 各種 <c>std_msgs/*MultiArray</c> の CDR シリアライズテスト。
/// bit-exact は空の MultiArrayLayout (dim=[], data_offset=0) をヘッダとして採用し、
/// data シーケンス部分の alignment も含めて wire フォーマットを検証する。
/// </summary>
public class MultiArrayTests
{
    // 空レイアウトの wire (8 バイト): dim count=0, data_offset=0
    private static readonly byte[] EmptyLayoutLE =
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static MultiArrayLayout EmptyLayout() => new([], 0);

    private static byte[] Serialize<T>(ICdrSerializer<T> serializer, in T value, CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[512];
        var w = new CdrWriter(buf, endian);
        serializer.Serialize(ref w, in value);
        return buf[..w.BytesWritten].ToArray();
    }

    private static T Deserialize<T>(
        ICdrSerializer<T> serializer,
        ReadOnlySpan<byte> bytes,
        CdrEndianness endian,
        CdrReadLimits? limits = null)
    {
        var r = new CdrReader(bytes, endian, limits: limits);
        serializer.Deserialize(ref r, out T value);
        return value;
    }

    [Fact]
    public void ByteMultiArray_LE_bit_exact()
    {
        var msg = new ByteMultiArray(EmptyLayout(), [0xAA, 0xBB, 0xCC]);
        var bytes = Serialize(ByteMultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);

        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x03, 0x00, 0x00, 0x00, // data count = 3
            0xAA, 0xBB, 0xCC,       // bytes
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void ByteMultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new ByteMultiArray(
            new MultiArrayLayout([new MultiArrayDimension("d", 3, 3)], 0),
            [1, 2, 3]);
        var bytes = Serialize(ByteMultiArraySerializer.Instance, in msg, endian);
        var read = Deserialize(ByteMultiArraySerializer.Instance, bytes, endian);
        read.Layout.Dim.Should().HaveCount(1);
        read.Layout.Dim[0].Label.Should().Be("d");
        read.Data.Should().Equal((byte)1, (byte)2, (byte)3);
    }

    [Fact]
    public void ByteMultiArray_data_payloadが上限を超えたら確保前に拒否する()
    {
        byte[] bytes =
        [
            .. EmptyLayoutLE,
            0x03, 0x00, 0x00, 0x00,
            0x01, 0x02, 0x03,
        ];

        Assert.Throws<InvalidDataException>(() =>
            Deserialize(
                ByteMultiArraySerializer.Instance,
                bytes,
                CdrEndianness.LittleEndian,
                new CdrReadLimits(maxSequenceBytes: 2)));
    }

    [Fact]
    public void UInt8MultiArray_LE_bit_exact()
    {
        var msg = new UInt8MultiArray(EmptyLayout(), [0x11, 0x22]);
        var bytes = Serialize(UInt8MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x02, 0x00, 0x00, 0x00,
            0x11, 0x22,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt8MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt8MultiArray(EmptyLayout(), [0x10, 0x20, 0x30]);
        var bytes = Serialize(UInt8MultiArraySerializer.Instance, in msg, endian);
        Deserialize(UInt8MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal((byte)0x10, (byte)0x20, (byte)0x30);
    }

    [Fact]
    public void Int8MultiArray_LE_bit_exact()
    {
        var msg = new Int8MultiArray(EmptyLayout(), [-1, 0, 1]);
        var bytes = Serialize(Int8MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x03, 0x00, 0x00, 0x00,
            0xFF, 0x00, 0x01,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int8MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Int8MultiArray(EmptyLayout(), [-128, -1, 0, 1, 127]);
        var bytes = Serialize(Int8MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Int8MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal((sbyte)-128, (sbyte)-1, (sbyte)0, (sbyte)1, (sbyte)127);
    }

    [Fact]
    public void Int16MultiArray_LE_bit_exact()
    {
        // 空 layout 後に int16 配列 [−1, 1]。dim count は 4-byte 境界済で整列 pad 不要。
        var msg = new Int16MultiArray(EmptyLayout(), [-1, 1]);
        var bytes = Serialize(Int16MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x02, 0x00, 0x00, 0x00, // data count
            0xFF, 0xFF,             // -1
            0x01, 0x00,             // 1
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int16MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Int16MultiArray(EmptyLayout(), [short.MinValue, -1, 0, 1, short.MaxValue]);
        var bytes = Serialize(Int16MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Int16MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void UInt16MultiArray_LE_bit_exact()
    {
        var msg = new UInt16MultiArray(EmptyLayout(), [0xBEEF, 0xCAFE]);
        var bytes = Serialize(UInt16MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x02, 0x00, 0x00, 0x00,
            0xEF, 0xBE,
            0xFE, 0xCA,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt16MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt16MultiArray(EmptyLayout(), [0, 0xFFFF, 0x1234]);
        var bytes = Serialize(UInt16MultiArraySerializer.Instance, in msg, endian);
        Deserialize(UInt16MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Int32MultiArray_LE_bit_exact()
    {
        var msg = new Int32MultiArray(EmptyLayout(), [1, 2]);
        var bytes = Serialize(Int32MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x02, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int32MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Int32MultiArray(EmptyLayout(), [int.MinValue, -1, 0, 1, int.MaxValue]);
        var bytes = Serialize(Int32MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Int32MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Int32MultiArray_data_countに対して残量不足なら確保前に拒否する()
    {
        byte[] bytes =
        [
            .. EmptyLayoutLE,
            0x02, 0x00, 0x00, 0x00,
        ];

        Assert.Throws<InvalidDataException>(() =>
            Deserialize(Int32MultiArraySerializer.Instance, bytes, CdrEndianness.LittleEndian));
    }

    [Fact]
    public void UInt32MultiArray_LE_bit_exact()
    {
        var msg = new UInt32MultiArray(EmptyLayout(), [0xDEADBEEFu]);
        var bytes = Serialize(UInt32MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,
            0xEF, 0xBE, 0xAD, 0xDE,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt32MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt32MultiArray(EmptyLayout(), [0u, uint.MaxValue, 0x12345678u]);
        var bytes = Serialize(UInt32MultiArraySerializer.Instance, in msg, endian);
        Deserialize(UInt32MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Int64MultiArray_LE_bit_exact_は_8バイト整列_padding_含む()
    {
        // layout (8B) → data count (4B, position 8-11) → padding (4B) → int64 要素
        var msg = new Int64MultiArray(EmptyLayout(), [1L]);
        var bytes = Serialize(Int64MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,             // count = 1
            0x00, 0x00, 0x00, 0x00,             // padding to 8-byte
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 1L
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int64MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Int64MultiArray(EmptyLayout(), [long.MinValue, -1, 0, 1, long.MaxValue]);
        var bytes = Serialize(Int64MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Int64MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void UInt64MultiArray_LE_bit_exact()
    {
        var msg = new UInt64MultiArray(EmptyLayout(), [0xCAFEBABE_DEADBEEFUL]);
        var bytes = Serialize(UInt64MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xEF, 0xBE, 0xAD, 0xDE, 0xBE, 0xBA, 0xFE, 0xCA,
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt64MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt64MultiArray(EmptyLayout(), [0UL, ulong.MaxValue, 0x1234567890ABCDEFUL]);
        var bytes = Serialize(UInt64MultiArraySerializer.Instance, in msg, endian);
        Deserialize(UInt64MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Float32MultiArray_LE_bit_exact()
    {
        var msg = new Float32MultiArray(EmptyLayout(), [1.0f]);
        var bytes = Serialize(Float32MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x80, 0x3F, // 1.0f (IEEE 754)
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Float32MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Float32MultiArray(EmptyLayout(), [0f, MathF.PI, float.NegativeInfinity, -0f]);
        var bytes = Serialize(Float32MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Float32MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Float64MultiArray_LE_bit_exact_は_8バイト整列_padding_含む()
    {
        var msg = new Float64MultiArray(EmptyLayout(), [1.0]);
        var bytes = Serialize(Float64MultiArraySerializer.Instance, in msg, CdrEndianness.LittleEndian);
        byte[] expected =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, // 1.0 (IEEE 754)
        ];
        bytes.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Float64MultiArray_roundtrip(CdrEndianness endian)
    {
        var msg = new Float64MultiArray(EmptyLayout(), [0.0, Math.E, double.NegativeInfinity]);
        var bytes = Serialize(Float64MultiArraySerializer.Instance, in msg, endian);
        Deserialize(Float64MultiArraySerializer.Instance, bytes, endian)
            .Data.Should().Equal(msg.Data);
    }

    [Fact]
    public void Float64MultiArray_data_payloadは8バイトalignment_padding込みで残量検証する()
    {
        byte[] bytes =
        [
            .. EmptyLayoutLE,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];

        Assert.Throws<InvalidDataException>(() =>
            Deserialize(Float64MultiArraySerializer.Instance, bytes, CdrEndianness.LittleEndian));
    }
}
