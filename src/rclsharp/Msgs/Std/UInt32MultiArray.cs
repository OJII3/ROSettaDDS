using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/UInt32MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// uint32[] data
/// </code>
/// </summary>
public struct UInt32MultiArray
{
    public const string RosTypeName = "std_msgs/msg/UInt32MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::UInt32MultiArray_";

    public MultiArrayLayout Layout;
    public uint[] Data;

    public UInt32MultiArray(MultiArrayLayout layout, uint[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.UInt32MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class UInt32MultiArraySerializer : ICdrSerializer<UInt32MultiArray>
{
    public static readonly UInt32MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in UInt32MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + count * 4;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in UInt32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<uint>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteUInt32(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out UInt32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 4, elementAlignment: 4);
        var data = count == 0 ? Array.Empty<uint>() : new uint[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadUInt32();
        }
        value = new UInt32MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in UInt32MultiArray value)
    {
    }
}
