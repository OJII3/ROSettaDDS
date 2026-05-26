using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/UInt64MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// uint64[] data
/// </code>
/// </summary>
public struct UInt64MultiArray
{
    public const string RosTypeName = "std_msgs/msg/UInt64MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::UInt64MultiArray_";

    public MultiArrayLayout Layout;
    public ulong[] Data;

    public UInt64MultiArray(MultiArrayLayout layout, ulong[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.UInt64MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class UInt64MultiArraySerializer : ICdrSerializer<UInt64MultiArray>
{
    public static readonly UInt64MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in UInt64MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + 4 + count * 8;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in UInt64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<ulong>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteUInt64(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out UInt64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 8, elementAlignment: 8);
        var data = count == 0 ? Array.Empty<ulong>() : new ulong[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadUInt64();
        }
        value = new UInt64MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in UInt64MultiArray value)
    {
    }
}
