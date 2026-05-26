using Rclsharp.Cdr;

namespace Rclsharp.Msgs.Std;

/// <summary>
/// std_msgs/msg/Float32MultiArray の C# 表現。
/// IDL:
/// <code>
/// std_msgs/MultiArrayLayout layout
/// float32[] data
/// </code>
/// </summary>
public struct Float32MultiArray
{
    public const string RosTypeName = "std_msgs/msg/Float32MultiArray";
    public const string DdsTypeName = "std_msgs::msg::dds_::Float32MultiArray_";

    public MultiArrayLayout Layout;
    public float[] Data;

    public Float32MultiArray(MultiArrayLayout layout, float[] data)
    {
        Layout = layout;
        Data = data;
    }

    public override string ToString() =>
        $"std_msgs.Float32MultiArray({Layout}, data[{(Data is null ? 0 : Data.Length)}])";
}

public sealed class Float32MultiArraySerializer : ICdrSerializer<Float32MultiArray>
{
    public static readonly Float32MultiArraySerializer Instance = new();

    public bool IsKeyed => false;

    public int GetSerializedSize(in Float32MultiArray value)
    {
        int total = MultiArrayLayoutSerializer.Instance.GetSerializedSize(in value.Layout);
        int count = value.Data is null ? 0 : value.Data.Length;
        total += 4 + count * 4;
        return total;
    }

    public void Serialize(ref CdrWriter writer, in Float32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Serialize(ref writer, in value.Layout);
        var data = value.Data ?? Array.Empty<float>();
        writer.WriteSequenceLength(data.Length);
        foreach (var v in data)
        {
            writer.WriteFloat(v);
        }
    }

    public void Deserialize(ref CdrReader reader, out Float32MultiArray value)
    {
        MultiArrayLayoutSerializer.Instance.Deserialize(ref reader, out MultiArrayLayout layout);
        int count = reader.ReadSequenceLength(elementSize: 4, elementAlignment: 4);
        var data = count == 0 ? Array.Empty<float>() : new float[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = reader.ReadFloat();
        }
        value = new Float32MultiArray(layout, data);
    }

    public void SerializeKey(ref CdrWriter writer, in Float32MultiArray value)
    {
    }
}
