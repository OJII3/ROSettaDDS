using Rclsharp.Cdr;

namespace Rclsharp.Tests.Cdr;

public class CdrStringSequenceTests
{
    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void string_往復(CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, endian);
        w.WriteString("Hello");
        w.WriteString("");
        w.WriteString("こんにちは"); // UTF-8

        var r = new CdrReader(buf[..w.BytesWritten], endian);
        r.ReadString().Should().Be("Hello");
        r.ReadString().Should().Be("");
        r.ReadString().Should().Be("こんにちは");
    }

    [Fact]
    public void string_の_LE_bit_exact_Hello_は_length6_NUL終端()
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteString("Hello");

        // length=6 (5 + NUL) を LE で 4B + "Hello" + NUL = 10B
        buf[..w.BytesWritten].ToArray().Should().Equal(
            0x06, 0x00, 0x00, 0x00,
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x00);
    }

    [Fact]
    public void string_の後に_int32_を書くと_4バイト境界に整列される()
    {
        Span<byte> buf = stackalloc byte[32];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteString("Hello"); // 10 バイト消費
        w.WriteInt32(0x04030201);

        // string 後の position は 10、4 境界へ 2 バイト pad 後に int32
        buf[..w.BytesWritten].ToArray().Should().Equal(
            0x06, 0x00, 0x00, 0x00,
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x00,
            0x00, 0x00, // padding
            0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void string_長が上限を超えたら拒否する()
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteString("abcd");
        byte[] bytes = buf[..w.BytesWritten].ToArray();

        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new CdrReader(
                bytes,
                CdrEndianness.LittleEndian,
                limits: new CdrReadLimits(maxStringBytes: 4));
            r.ReadString();
        });
    }

    [Fact]
    public void string_lengthが残量を超えたら拒否する()
    {
        byte[] bytes = [0x05, 0x00, 0x00, 0x00, (byte)'a'];

        Assert.Throws<InvalidOperationException>(() =>
        {
            var r = new CdrReader(bytes, CdrEndianness.LittleEndian);
            r.ReadString();
        });
    }

    [Fact]
    public void string_NUL終端がなければ拒否する()
    {
        byte[] bytes = [0x02, 0x00, 0x00, 0x00, (byte)'a', (byte)'b'];

        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new CdrReader(bytes, CdrEndianness.LittleEndian);
            r.ReadString();
        });
    }

    [Fact]
    public void sequence_長が上限を超えたら拒否する()
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
            r.ReadSequenceLength();
        });
    }

    [Fact]
    public void 固定長sequence_payloadが残量を超えたら確保前に拒否する()
    {
        Span<byte> buf = stackalloc byte[8];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteSequenceLength(2);
        byte[] bytes = buf[..w.BytesWritten].ToArray();

        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new CdrReader(bytes, CdrEndianness.LittleEndian);
            r.ReadSequenceLength(elementSize: 4, elementAlignment: 4);
        });
    }

    [Fact]
    public void 固定長sequence_payloadはalignment_padding込みで残量を検証する()
    {
        byte[] bytes =
        [
            0x01, 0x00, 0x00, 0x00, // count = 1
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new CdrReader(bytes, CdrEndianness.LittleEndian);
            r.ReadSequenceLength(elementSize: 8, elementAlignment: 8);
        });
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void sequence_長_と_要素の往復(CdrEndianness endian)
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, endian);
        int[] elements = [10, 20, 30, 40];
        w.WriteSequenceLength(elements.Length);
        foreach (var e in elements) w.WriteInt32(e);

        var r = new CdrReader(buf[..w.BytesWritten], endian);
        int count = r.ReadSequenceLength();
        count.Should().Be(4);
        var read = new int[count];
        for (int i = 0; i < count; i++) read[i] = r.ReadInt32();
        read.Should().Equal(elements);
    }

    [Fact]
    public void sequence_LE_bit_exact_4要素_int32()
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteSequenceLength(2);
        w.WriteInt32(0x01020304);
        w.WriteInt32(0x05060708);

        buf[..w.BytesWritten].ToArray().Should().Equal(
            0x02, 0x00, 0x00, 0x00,
            0x04, 0x03, 0x02, 0x01,
            0x08, 0x07, 0x06, 0x05);
    }
}
