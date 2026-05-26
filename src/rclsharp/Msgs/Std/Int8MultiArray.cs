using System.Runtime.InteropServices;
using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Int8MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// int8[] data
/// </code>
/// </summary>
public struct Int8MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Int8MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Int8MultiArray_";

    public MultiArrayLayout Layout;
    public sbyte[] Data;

    public Int8MultiArray(MultiArrayLayout layout, sbyte[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Int8MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Int8MultiArraySerializer : ICdrSerializer<Int8MultiArray>
{
    public static readonly Int8MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Int8MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        total += 4 + (value.Data is null ? 0 : value.Data.Length);
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Int8MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<sbyte>();
        writer.WriteSequenceLength(data.Length);
        writer.WriteRawBytes(MemoryMarshal.AsBytes(data.AsSpan()));
    }

    public void Deserialize(ref CdrReader reader, out Int8MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 1, elementAlignment: 1);
        var data = count == 0 ? Array.Empty<sbyte>() : new sbyte[count];
        if (count > 0)
        {
            reader.ReadRawBytes(count).CopyTo(MemoryMarshal.AsBytes(data.AsSpan()));
        }
        value = new Int8MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Int8MultiArray value)
    {
    }
}
