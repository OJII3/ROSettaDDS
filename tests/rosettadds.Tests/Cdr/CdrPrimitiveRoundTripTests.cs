using ROSettaDDS.Cdr;

namespace ROSettaDDS.Tests.Cdr;

public class CdrPrimitiveRoundTripTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void byte_sbyte_bool_の往復(CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new CdrWriter(buf, endian);
        w.WriteByte(0xFE);
        w.WriteSByte(-7);
        w.WriteBool(true);
        w.WriteBool(false);

        var r = new CdrReader(buf[..w.BytesWritten], endian);
        r.ReadByte().Should().Be((byte)0xFE);
        r.ReadSByte().Should().Be((sbyte)-7);
        r.ReadBool().Should().BeTrue();
        r.ReadBool().Should().BeFalse();
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void int16_int32_int64_の往復(CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, endian);
        w.WriteInt16(-1234);
        w.WriteUInt16(0xBEEF);
        w.WriteInt32(int.MinValue);
        w.WriteUInt32(0xDEADBEEFu);
        w.WriteInt64(long.MinValue);
        w.WriteUInt64(0xCAFEBABE_DEADBEEFu);

        var r = new CdrReader(buf[..w.BytesWritten], endian);
        r.ReadInt16().Should().Be((short)-1234);
        r.ReadUInt16().Should().Be((ushort)0xBEEF);
        r.ReadInt32().Should().Be(int.MinValue);
        r.ReadUInt32().Should().Be(0xDEADBEEFu);
        r.ReadInt64().Should().Be(long.MinValue);
        r.ReadUInt64().Should().Be(0xCAFEBABE_DEADBEEFu);
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void float_double_の往復(CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[32];
        var w = new CdrWriter(buf, endian);
        w.WriteFloat(MathF.PI);
        w.WriteDouble(Math.E);
        w.WriteFloat(float.NaN);
        w.WriteDouble(double.NegativeInfinity);

        var r = new CdrReader(buf[..w.BytesWritten], endian);
        r.ReadFloat().Should().Be(MathF.PI);
        r.ReadDouble().Should().Be(Math.E);
        float.IsNaN(r.ReadFloat()).Should().BeTrue();
        r.ReadDouble().Should().Be(double.NegativeInfinity);
    }

    [Fact]
    public void byte_に続く_int32_は_3バイトの_ゼロ_padding_が入る_LE()
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteByte(0x01);
        w.WriteInt32(0x04030201);

        w.BytesWritten.Should().Be(8);
        buf[..8].ToArray().Should().Equal(0x01, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void byte_に続く_int32_は_3バイトの_ゼロ_padding_が入る_BE()
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new CdrWriter(buf, CdrEndianness.BigEndian);
        w.WriteByte(0x01);
        w.WriteInt32(0x04030201);

        w.BytesWritten.Should().Be(8);
        buf[..8].ToArray().Should().Equal(0x01, 0x00, 0x00, 0x00, 0x04, 0x03, 0x02, 0x01);
    }

    [Fact]
    public void cdrOrigin_を_4_に設定すると_最初の_4バイトは無視されて整列が計算される()
    {
        Span<byte> buf = stackalloc byte[16];
        // カプセルヘッダ相当の 4 バイト分はスキップした状態で開始
        buf[0] = 0xCA; buf[1] = 0xFE; buf[2] = 0xBA; buf[3] = 0xBE;
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian, cdrOrigin: 4);
        w.WriteByte(0x01);
        w.WriteInt32(0x04030201);

        w.Position.Should().Be(12);
        w.BytesWritten.Should().Be(8);
        // 先頭 4B はカプセルヘッダ風、その後は通常の CDR
        buf[..12].ToArray().Should().Equal(0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void バッファ枯渇は_InvalidOperationException()
    {
        Span<byte> buf = stackalloc byte[2];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteByte(0x01);
        w.WriteByte(0x02);

        try
        {
            w.WriteByte(0x03);
            Assert.Fail("should have thrown");
        }
        catch (InvalidOperationException) { /* expected */ }
    }

    [Fact]
    public void Reader_の_読み出し過剰は_InvalidOperationException()
    {
        ReadOnlySpan<byte> buf = stackalloc byte[1] { 0x01 };
        var r = new CdrReader(buf, CdrEndianness.LittleEndian);
        r.ReadByte();

        try
        {
            r.ReadByte();
            Assert.Fail("should have thrown");
        }
        catch (InvalidOperationException) { /* expected */ }
    }
}
