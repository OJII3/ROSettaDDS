using ROSettaDDS.Cdr;
using ROSettaDDS.Msgs.BuiltinInterfaces;
using ROSettaDDS.Msgs.Sample; // sample_msgs → ROSettaDDS.Msgs.Sample (Source Generator が生成)
using ROSettaDDS.Msgs.Std;

// msgs/sample_msgs/msg/Demo.msg から Source Generator が生成した Demo / DemoSerializer を使う。
var demo = new Demo(
    new Header(new Time(12, 34u), "sensor_frame"),
    "custom message",
    new[] { 1.5, 2.5, 3.5 },
    7);

Span<byte> buffer = stackalloc byte[DemoSerializer.Instance.GetSerializedSize(in demo)];
var writer = new CdrWriter(buffer, CdrEndianness.LittleEndian);
DemoSerializer.Instance.Serialize(ref writer, in demo);
byte[] wire = buffer.Slice(0, writer.BytesWritten).ToArray();

Console.WriteLine($"serialized {wire.Length} bytes");

var reader = new CdrReader(wire, CdrEndianness.LittleEndian);
DemoSerializer.Instance.Deserialize(ref reader, out Demo roundTrip);

Console.WriteLine($"roundtrip: {roundTrip}");
Console.WriteLine($"  frame_id = {roundTrip.Header.FrameId}");
Console.WriteLine($"  values   = [{string.Join(", ", roundTrip.Values)}]");
Console.WriteLine($"  count    = {roundTrip.Count}");
