using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Int16MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// int16[] data
/// </code>
/// </summary>
public struct Int16MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Int16MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Int16MultiArray_";

    public MultiArrayLayout Layout;
    public short[] Data;

    public Int16MultiArray(MultiArrayLayout layout, short[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Int16MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Int16MultiArraySerializer : ICdrSerializer<Int16MultiArray>
{
    public static readonly Int16MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Int16MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + count * 2 + 2; // +2 = 最悪の alignment padding
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Int16MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<short>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteInt16(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out Int16MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 2, elementAlignment: 2);
        var data = count == 0 ? Array.Empty<short>() : new short[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadInt16();
        }
        value = new Int16MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Int16MultiArray value)
    {
    }
}
