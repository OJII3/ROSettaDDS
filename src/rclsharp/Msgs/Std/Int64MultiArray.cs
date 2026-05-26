using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Int64MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// int64[] data
/// </code>
/// </summary>
public struct Int64MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Int64MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Int64MultiArray_";

    public MultiArrayLayout Layout;
    public long[] Data;

    public Int64MultiArray(MultiArrayLayout layout, long[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Int64MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Int64MultiArraySerializer : ICdrSerializer<Int64MultiArray>
{
    public static readonly Int64MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Int64MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + 4 + count * 8; // +4 = 最悪の 8 境界 padding
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Int64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<long>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteInt64(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out Int64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 8, elementAlignment: 8);
        var data = count == 0 ? Array.Empty<long>() : new long[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadInt64();
        }
        value = new Int64MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Int64MultiArray value)
    {
    }
}
