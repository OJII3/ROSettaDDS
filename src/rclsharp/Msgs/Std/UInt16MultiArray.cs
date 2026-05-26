using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/UInt16MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// uint16[] data
/// </code>
/// </summary>
public struct UInt16MultiArray
{
    public const string RosTypeName = "std_msgs/msg/UInt16MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::UInt16MultiArray_";

    public MultiArrayLayout Layout;
    public ushort[] Data;

    public UInt16MultiArray(MultiArrayLayout layout, ushort[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.UInt16MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class UInt16MultiArraySerializer : ICdrSerializer<UInt16MultiArray>
{
    public static readonly UInt16MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in UInt16MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + count * 2 + 2;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in UInt16MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<ushort>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteUInt16(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out UInt16MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 2, elementAlignment: 2);
        var data = count == 0 ? Array.Empty<ushort>() : new ushort[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadUInt16();
        }
        value = new UInt16MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in UInt16MultiArray value)
    {
    }
}
