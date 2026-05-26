using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/ByteMultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// byte[] data
/// </code>
/// </summary>
public struct ByteMultiArray
{
    public const string RosTypeName = "std_msgs/msg/ByteMultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::ByteMultiArray_";

    public MultiArrayLayout Layout;
    public byte[] Data;

    public ByteMultiArray(MultiArrayLayout layout, byte[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.ByteMultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class ByteMultiArraySerializer : ICdrSerializer<ByteMultiArray>
{
    public static readonly ByteMultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in ByteMultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        total += 4 + (value.Data is null ? 0 : value.Data.Length);
        return total;
    }

    public void Serialize(ref CdrWriter writer, in ByteMultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<byte>();
        writer.WriteSequenceLength(data.Length);
        writer.WriteRawBytes(data);
    }

    public void Deserialize(ref CdrReader reader, out ByteMultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 1, elementAlignment: 1);
        var data = count == 0 ? Array.Empty<byte>() : reader.ReadRawBytes(count).ToArray();
        value = new ByteMultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in ByteMultiArray value)
    {
    }
}
