using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/UInt8MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// uint8[] data
/// </code>
/// </summary>
public struct UInt8MultiArray
{
    public const string RosTypeName = "std_msgs/msg/UInt8MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::UInt8MultiArray_";

    public MultiArrayLayout Layout;
    public byte[] Data;

    public UInt8MultiArray(MultiArrayLayout layout, byte[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.UInt8MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class UInt8MultiArraySerializer : ICdrSerializer<UInt8MultiArray>
{
    public static readonly UInt8MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in UInt8MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        total += 4 + (value.Data is null ? 0 : value.Data.Length);
        return total;
    }

    public void Serialize(ref CdrWriter writer, in UInt8MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<byte>();
        writer.WriteSequenceLength(data.Length);
        writer.WriteRawBytes(data);
    }

    public void Deserialize(ref CdrReader reader, out UInt8MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 1, elementAlignment: 1);
        var data = count == 0 ? Array.Empty<byte>() : reader.ReadRawBytes(count).ToArray();
        value = new UInt8MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in UInt8MultiArray value)
    {
    }
}
