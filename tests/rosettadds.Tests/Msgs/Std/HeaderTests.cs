using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.BuiltinInterfaces;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Msgs.Std;

/// <summary>
/// std_msgs/Header の CDR シリアライズテスト。
/// ROS 2 版のため <c>seq</c> は持たず、stamp (Time) + frame_id (string) のみ。
/// </summary>
public class HeaderTests
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
    public void Header_LE_bit_exact()
    {
        var h = new Header(new Time(1, 2u), "x");
        Serialize(HeaderSerializer.Instance, in h, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x01, 0x00, 0x00, 0x00,            // sec = 1
                0x02, 0x00, 0x00, 0x00,            // nanosec = 2
                0x02, 0x00, 0x00, 0x00,            // frame_id length (incl NUL) = 2
                (byte)'x', 0x00);                  // "x\0"
    }

    [Fact]
    public void Header_空文字列_LE_bit_exact()
    {
        var h = new Header(new Time(0, 0u), string.Empty);
        Serialize(HeaderSerializer.Instance, in h, CdrEndianness.LittleEndian)
            .Should().Equal(
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x00, 0x00,            // length = 1 (NUL only)
                0x00);                             // "\0"
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Header_roundtrip(CdrEndianness endian)
    {
        var h = new Header(new Time(1_700_000_000, 123_456_789u), "base_link");
        var bytes = Serialize(HeaderSerializer.Instance, in h, endian);
        var read = Deserialize(HeaderSerializer.Instance, bytes, endian);
        read.Stamp.Sec.Should().Be(1_700_000_000);
        read.Stamp.Nanosec.Should().Be(123_456_789u);
        read.FrameId.Should().Be("base_link");
    }

    [Theory]
    [InlineData(CdrEndianness.LittleEndian)]
    [InlineData(CdrEndianness.BigEndian)]
    public void Header_マルチバイト文字_roundtrip(CdrEndianness endian)
    {
        var h = new Header(new Time(0, 0u), "日本語フレーム");
        var bytes = Serialize(HeaderSerializer.Instance, in h, endian);
        var read = Deserialize(HeaderSerializer.Instance, bytes, endian);
        read.FrameId.Should().Be("日本語フレーム");
    }

    [Fact]
    public void Header_GetSerializedSize_は_stamp_と_frame_id_長さで決まる()
    {
        // stamp(8) + length(4) + "x"(1) + NUL(1) = 14
        var h = new Header(new Time(0, 0u), "x");
        HeaderSerializer.Instance.GetSerializedSize(in h).Should().Be(14);

        // 空文字列でも length + NUL の 5B は必要: 8 + 4 + 0 + 1 = 13
        var empty = new Header(new Time(0, 0u), string.Empty);
        HeaderSerializer.Instance.GetSerializedSize(in empty).Should().Be(13);
    }

    [Fact]
    public void Header_ROS2版は_seq_フィールドを持たない()
    {
        // ROS 1 の Header は uint32 seq + stamp + frame_id で最低 4+8+4+NUL=17B だが、
        // ROS 2 版は seq 無しのため stamp(8) + length(4) + "x"(1) + NUL(1) = 14B になる。
        var h = new Header(new Time(0, 0u), "x");
        var bytes = Serialize(HeaderSerializer.Instance, in h, CdrEndianness.LittleEndian);
        bytes.Length.Should().Be(14);
    }
}
