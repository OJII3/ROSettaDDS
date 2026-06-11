using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.BuiltinInterfaces;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Integration;

/// <summary>
/// ROS 2 (rclpy) が serialize_message で出力した CDR バイト列と
/// rosettadds の Serialize 結果が bit-exact で一致することを検証するテスト。
/// fixture バイナリは encapsulation header (4B) + payload の形式。
/// </summary>
public class WireBitExactTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Integration", "Fixtures");

    private static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDir, name));

    /// <summary>
    /// fixture から encapsulation header を読み取り、endianness と payload を返す。
    /// </summary>
    private static (CdrEndianness endianness, byte[] payload) ParseFixture(byte[] raw)
    {
        var (kind, _) = CdrEncapsulation.Read(raw);
        CdrEncapsulation.IsParameterList(kind).Should().BeFalse("Plain CDR fixture expected");
        var endianness = CdrEncapsulation.GetEndianness(kind);
        var payload = raw[CdrEncapsulation.Size..];
        return (endianness, payload);
    }

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
        r.Remaining.Should().Be(0, "deserializer must consume all payload bytes");
        return value;
    }

    // ── std_msgs/String ──

    [Fact]
    public void StringMessage_HelloWorld_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("std_msgs_String.bin"));
        var msg = new StringMessage("Hello World");
        var actual = Serialize(StringMessageSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void StringMessage_空文字列_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("std_msgs_String_empty.bin"));
        var msg = new StringMessage("");
        var actual = Serialize(StringMessageSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void StringMessage_ROS2出力からデシリアライズできる()
    {
        var (endianness, payload) = ParseFixture(LoadFixture("std_msgs_String.bin"));
        var msg = Deserialize(StringMessageSerializer.Instance, payload, endianness);
        msg.Data.Should().Be("Hello World");
    }

    // ── builtin_interfaces/Time ──

    [Fact]
    public void Time_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("builtin_interfaces_Time.bin"));
        var msg = new Time(1234567890, 123456789u);
        var actual = Serialize(TimeSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Time_ROS2出力からデシリアライズできる()
    {
        var (endianness, payload) = ParseFixture(LoadFixture("builtin_interfaces_Time.bin"));
        var msg = Deserialize(TimeSerializer.Instance, payload, endianness);
        msg.Sec.Should().Be(1234567890);
        msg.Nanosec.Should().Be(123456789u);
    }

    // ── std_msgs/Header ──

    [Fact]
    public void Header_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("std_msgs_Header.bin"));
        var msg = new Header(new Time(1234567890, 123456789u), "map");
        var actual = Serialize(HeaderSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Header_空frameId_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("std_msgs_Header_empty.bin"));
        var msg = new Header(new Time(0, 0u), "");
        var actual = Serialize(HeaderSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Header_ROS2出力からデシリアライズできる()
    {
        var (endianness, payload) = ParseFixture(LoadFixture("std_msgs_Header.bin"));
        var msg = Deserialize(HeaderSerializer.Instance, payload, endianness);
        msg.Stamp.Sec.Should().Be(1234567890);
        msg.Stamp.Nanosec.Should().Be(123456789u);
        msg.FrameId.Should().Be("map");
    }

    // ── std_msgs/ColorRGBA ──

    [Fact]
    public void ColorRgba_はROS2出力と一致する()
    {
        var (endianness, expected) = ParseFixture(LoadFixture("std_msgs_ColorRGBA.bin"));
        var msg = new ColorRgba(1.0f, 0.5f, 0.25f, 0.75f);
        var actual = Serialize(ColorRgbaSerializer.Instance, in msg, endianness);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void ColorRgba_ROS2出力からデシリアライズできる()
    {
        var (endianness, payload) = ParseFixture(LoadFixture("std_msgs_ColorRGBA.bin"));
        var msg = Deserialize(ColorRgbaSerializer.Instance, payload, endianness);
        msg.R.Should().Be(1.0f);
        msg.G.Should().Be(0.5f);
        msg.B.Should().Be(0.25f);
        msg.A.Should().Be(0.75f);
    }
}
