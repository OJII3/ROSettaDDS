using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Float64MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// float64[] data
/// </code>
/// </summary>
public struct Float64MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Float64MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Float64MultiArray_";

    public MultiArrayLayout Layout;
    public double[] Data;

    public Float64MultiArray(MultiArrayLayout layout, double[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Float64MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Float64MultiArraySerializer : ICdrSerializer<Float64MultiArray>
{
    public static readonly Float64MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Float64MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + 4 + count * 8;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Float64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<double>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteDouble(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out Float64MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 8, elementAlignment: 8);
        var data = count == 0 ? Array.Empty<double>() : new double[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadDouble();
        }
        value = new Float64MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Float64MultiArray value)
    {
    }
}
