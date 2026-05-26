using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Int32MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// int32[] data
/// </code>
/// </summary>
public struct Int32MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Int32MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Int32MultiArray_";

    public MultiArrayLayout Layout;
    public int[] Data;

    public Int32MultiArray(MultiArrayLayout layout, int[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Int32MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Int32MultiArraySerializer : ICdrSerializer<Int32MultiArray>
{
    public static readonly Int32MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Int32MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + count * 4;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Int32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<int>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteInt32(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out Int32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 4, elementAlignment: 4);
        var data = count == 0 ? Array.Empty<int>() : new int[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadInt32();
        }
        value = new Int32MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Int32MultiArray value)
    {
    }
}
