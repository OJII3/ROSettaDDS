using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;

namespace ROSettaDDS.Tests.Rtps;

public class SequenceNumberSetTests
{
    [Fact]
    public void numBits_と_bitmap_長の整合性違反は例外()
    {
        Action act = () => new SequenceNumberSet(new SequenceNumber(1L), 33, new uint[] { 0 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void numBits_範囲外は例外()
    {
        Action over = () => new SequenceNumberSet(new SequenceNumber(1L), 257, new uint[9]);
        Action under = () => new SequenceNumberSet(new SequenceNumber(1L), -1, Array.Empty<uint>());
        over.Should().Throw<ArgumentOutOfRangeException>();
        under.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsSet_は_MSB_first_で判定する()
    {
        // bit 0 = MSB of bitmap[0] = 0x80000000
        var set = new SequenceNumberSet(new SequenceNumber(100L), 32, new uint[] { 0x80000000u });
        set.IsSet(0).Should().BeTrue();
        set.IsSet(1).Should().BeFalse();
        set.IsSet(31).Should().BeFalse();
    }

    [Fact]
    public void EnumerateSet_は_set_されたシーケンスを返す()
    {
        // bits 0, 5, 31 を set
        uint word = (1u << 31) | (1u << (31 - 5)) | 1u;
        var set = new SequenceNumberSet(new SequenceNumber(10L), 32, new uint[] { word });
        var values = set.EnumerateSet().Select(s => s.Value).ToArray();
        values.Should().Equal(10L, 15L, 41L);
    }

    [Fact]
    public void Write_と_Read_の往復_LE()
    {
        var src = new SequenceNumberSet(new SequenceNumber(42L), 64, new uint[] { 0xDEADBEEF, 0xCAFEBABE });
        var buf = new byte[src.SerializedSize];
        src.WriteTo(buf, littleEndian: true);

        var read = SequenceNumberSet.Read(buf, littleEndian: true, out int bytesRead);
        bytesRead.Should().Be(src.SerializedSize);
        read.BitmapBase.Should().Be(src.BitmapBase);
        read.NumBits.Should().Be(64);
        read.Bitmap.Should().Equal(0xDEADBEEFu, 0xCAFEBABEu);
    }

    [Fact]
    public void numBits_0_は_bitmap_長_0()
    {
        var src = new SequenceNumberSet(new SequenceNumber(1L), 0, Array.Empty<uint>());
        src.SerializedSize.Should().Be(12);

        var buf = new byte[12];
        src.WriteTo(buf, littleEndian: true);
        var read = SequenceNumberSet.Read(buf, littleEndian: true, out int br);
        br.Should().Be(12);
        read.NumBits.Should().Be(0);
        read.Bitmap.Should().BeEmpty();
    }
}
