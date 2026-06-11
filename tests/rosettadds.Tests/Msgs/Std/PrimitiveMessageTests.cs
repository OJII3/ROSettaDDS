using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Msgs.Std;

/// <summary>
/// std_msgs のプリミティブラッパ型の CDR シリアライズテスト。
/// - LE bit-exact: wire フォーマットが OMG CDR / rosidl と一致することを確認
/// - 往復 (LE/BE): 元の値に戻ることを確認
/// </summary>
public class PrimitiveMessageTests
{
    private static byte[] Serialize<T>(ICdrSerializer<T> serializer, in T value, CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[256];
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
    public void Bool_LE_bit_exact()
    {
        Serialize(BoolMessageSerializer.Instance, new BoolMessage(true), CdrEndianness.LittleEndian)
            .Should().Equal(0x01);
        Serialize(BoolMessageSerializer.Instance, new BoolMessage(false), CdrEndianness.LittleEndian)
            .Should().Equal(0x00);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian, true)]
    [InlineData(CdrEndianness.LittleEndian, false)]
    [InlineData(CdrEndianness.BigEndian, true)]
    [InlineData(CdrEndianness.BigEndian, false)]
    public void Bool_roundtrip(CdrEndianness endian, bool value)
    {
        var msg = new BoolMessage(value);
        var bytes = Serialize(BoolMessageSerializer.Instance, in msg, endian);
        Deserialize(BoolMessageSerializer.Instance, bytes, endian).Data.Should().Be(value);
    }

    [Fact]
    public void Byte_LE_bit_exact()
    {
        Serialize(ByteMessageSerializer.Instance, new ByteMessage(0xAB), CdrEndianness.LittleEndian)
            .Should().Equal(0xAB);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Byte_roundtrip(CdrEndianness endian)
    {
        var msg = new ByteMessage(0x7F);
        var bytes = Serialize(ByteMessageSerializer.Instance, in msg, endian);
        Deserialize(ByteMessageSerializer.Instance, bytes, endian).Data.Should().Be((byte)0x7F);
    }

    [Fact]
    public void Char_LE_bit_exact()
    {
        Serialize(CharMessageSerializer.Instance, new CharMessage(-1), CdrEndianness.LittleEndian)
            .Should().Equal(0xFF);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Char_roundtrip(CdrEndianness endian)
    {
        var msg = new CharMessage(-42);
        var bytes = Serialize(CharMessageSerializer.Instance, in msg, endian);
        Deserialize(CharMessageSerializer.Instance, bytes, endian).Data.Should().Be((sbyte)-42);
    }

    [Fact]
    public void Empty_は_1_バイトのダミー()
    {
        // rosidl の空 struct は 1 バイトの 0x00 プレースホルダを含む
        Serialize(EmptyMessageSerializer.Instance, new EmptyMessage(), CdrEndianness.LittleEndian)
            .Should().Equal(0x00);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Empty_roundtrip(CdrEndianness endian)
    {
        var bytes = Serialize(EmptyMessageSerializer.Instance, new EmptyMessage(), endian);
        bytes.Should().HaveCount(1);
        // デシリアライズで例外が出ないことだけ確認
        Deserialize(EmptyMessageSerializer.Instance, bytes, endian);
    }

    [Fact]
    public void Int8_LE_bit_exact()
    {
        Serialize(Int8MessageSerializer.Instance, new Int8Message(-128), CdrEndianness.LittleEndian)
            .Should().Equal(0x80);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int8_roundtrip(CdrEndianness endian)
    {
        var msg = new Int8Message(-7);
        var bytes = Serialize(Int8MessageSerializer.Instance, in msg, endian);
        Deserialize(Int8MessageSerializer.Instance, bytes, endian).Data.Should().Be((sbyte)-7);
    }

    [Fact]
    public void UInt8_LE_bit_exact()
    {
        Serialize(UInt8MessageSerializer.Instance, new UInt8Message(0xFE), CdrEndianness.LittleEndian)
            .Should().Equal(0xFE);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt8_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt8Message(0xA5);
        var bytes = Serialize(UInt8MessageSerializer.Instance, in msg, endian);
        Deserialize(UInt8MessageSerializer.Instance, bytes, endian).Data.Should().Be((byte)0xA5);
    }

    [Fact]
    public void Int16_LE_bit_exact()
    {
        Serialize(Int16MessageSerializer.Instance, new Int16Message(-1234), CdrEndianness.LittleEndian)
            .Should().Equal(0x2E, 0xFB); // -1234 = 0xFB2E
    }

    [Fact]
    public void Int16_BE_bit_exact()
    {
        Serialize(Int16MessageSerializer.Instance, new Int16Message(-1234), CdrEndianness.BigEndian)
            .Should().Equal(0xFB, 0x2E);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int16_roundtrip(CdrEndianness endian)
    {
        var msg = new Int16Message(short.MinValue);
        var bytes = Serialize(Int16MessageSerializer.Instance, in msg, endian);
        Deserialize(Int16MessageSerializer.Instance, bytes, endian).Data.Should().Be(short.MinValue);
    }

    [Fact]
    public void UInt16_LE_bit_exact()
    {
        Serialize(UInt16MessageSerializer.Instance, new UInt16Message(0xBEEF), CdrEndianness.LittleEndian)
            .Should().Equal(0xEF, 0xBE);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt16_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt16Message(0xCAFE);
        var bytes = Serialize(UInt16MessageSerializer.Instance, in msg, endian);
        Deserialize(UInt16MessageSerializer.Instance, bytes, endian).Data.Should().Be((ushort)0xCAFE);
    }

    [Fact]
    public void Int32_LE_bit_exact()
    {
        Serialize(Int32MessageSerializer.Instance, new Int32Message(0x04030201), CdrEndianness.LittleEndian)
            .Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int32_roundtrip(CdrEndianness endian)
    {
        var msg = new Int32Message(int.MinValue);
        var bytes = Serialize(Int32MessageSerializer.Instance, in msg, endian);
        Deserialize(Int32MessageSerializer.Instance, bytes, endian).Data.Should().Be(int.MinValue);
    }

    [Fact]
    public void UInt32_LE_bit_exact()
    {
        Serialize(UInt32MessageSerializer.Instance, new UInt32Message(0xDEADBEEFu), CdrEndianness.LittleEndian)
            .Should().Equal(0xEF, 0xBE, 0xAD, 0xDE);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt32_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt32Message(0x12345678u);
        var bytes = Serialize(UInt32MessageSerializer.Instance, in msg, endian);
        Deserialize(UInt32MessageSerializer.Instance, bytes, endian).Data.Should().Be(0x12345678u);
    }

    [Fact]
    public void Int64_LE_bit_exact()
    {
        Serialize(Int64MessageSerializer.Instance, new Int64Message(0x0807060504030201L), CdrEndianness.LittleEndian)
            .Should().Equal(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Int64_roundtrip(CdrEndianness endian)
    {
        var msg = new Int64Message(long.MinValue);
        var bytes = Serialize(Int64MessageSerializer.Instance, in msg, endian);
        Deserialize(Int64MessageSerializer.Instance, bytes, endian).Data.Should().Be(long.MinValue);
    }

    [Fact]
    public void UInt64_LE_bit_exact()
    {
        Serialize(UInt64MessageSerializer.Instance, new UInt64Message(0xCAFEBABE_DEADBEEFUL), CdrEndianness.LittleEndian)
            .Should().Equal(0xEF, 0xBE, 0xAD, 0xDE, 0xBE, 0xBA, 0xFE, 0xCA);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void UInt64_roundtrip(CdrEndianness endian)
    {
        var msg = new UInt64Message(ulong.MaxValue);
        var bytes = Serialize(UInt64MessageSerializer.Instance, in msg, endian);
        Deserialize(UInt64MessageSerializer.Instance, bytes, endian).Data.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Float32_LE_bit_exact()
    {
        // 1.0f = 0x3F800000 (IEEE 754)
        Serialize(Float32MessageSerializer.Instance, new Float32Message(1.0f), CdrEndianness.LittleEndian)
            .Should().Equal(0x00, 0x00, 0x80, 0x3F);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Float32_roundtrip(CdrEndianness endian)
    {
        var msg = new Float32Message(MathF.PI);
        var bytes = Serialize(Float32MessageSerializer.Instance, in msg, endian);
        Deserialize(Float32MessageSerializer.Instance, bytes, endian).Data.Should().Be(MathF.PI);
    }

    [Fact]
    public void Float64_LE_bit_exact()
    {
        // 1.0 = 0x3FF0000000000000 (IEEE 754)
        Serialize(Float64MessageSerializer.Instance, new Float64Message(1.0), CdrEndianness.LittleEndian)
            .Should().Equal(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Float64_roundtrip(CdrEndianness endian)
    {
        var msg = new Float64Message(Math.E);
        var bytes = Serialize(Float64MessageSerializer.Instance, in msg, endian);
        Deserialize(Float64MessageSerializer.Instance, bytes, endian).Data.Should().Be(Math.E);
    }
}
