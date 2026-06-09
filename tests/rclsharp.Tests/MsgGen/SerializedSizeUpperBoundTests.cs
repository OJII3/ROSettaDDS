using Rclsharp.Cdr;
using Rclsharp.Msgs.BuiltinInterfaces;
using Rclsharp.Msgs.Std;

namespace Rclsharp.Tests.MsgGen;

/// <summary>
/// 生成された <c>GetSerializedSize</c> が、実際のシリアライズ長以上 (上限) であることを
/// 文字列長や配列要素数を変えながら検証する。整列パディングの見積り漏れによる
/// バッファ過小確保 (under-allocation) を防ぐ。
/// </summary>
public class SerializedSizeUpperBoundTests
{
    private static int Serialized<T>(ICdrSerializer<T> ser, in T value)
    {
        var buf = new byte[4096];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ser.Serialize(ref w, in value);
        return w.BytesWritten;
    }

    private static void AssertUpperBound<T>(ICdrSerializer<T> ser, in T value)
    {
        ser.GetSerializedSize(in value).Should().BeGreaterThanOrEqualTo(Serialized(ser, in value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("base_link")]
    [InlineData("日本語フレーム")]
    public void Header_は上限を満たす(string frameId)
    {
        AssertUpperBound(HeaderSerializer.Instance, new Header(new Time(1, 2u), frameId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void Float32MultiArray_は上限を満たす(int n)
    {
        var dims = new[] { new MultiArrayDimension("rows", 1u, 2u), new MultiArrayDimension("longer_label", 3u, 4u) };
        var layout = new MultiArrayLayout(dims, 5u);
        var data = new float[n];
        for (int i = 0; i < n; i++) data[i] = i * 1.5f;
        AssertUpperBound(Float32MultiArraySerializer.Instance, new Float32MultiArray(layout, data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void MultiArrayLayout_は上限を満たす(int dimCount)
    {
        var dims = new MultiArrayDimension[dimCount];
        for (int i = 0; i < dimCount; i++) dims[i] = new MultiArrayDimension($"dim_{i}", (uint)i, (uint)i);
        AssertUpperBound(MultiArrayLayoutSerializer.Instance, new MultiArrayLayout(dims, 9u));
    }

    [Fact]
    public void ByteMultiArray_は上限を満たす()
    {
        var layout = new MultiArrayLayout(new[] { new MultiArrayDimension("a", 1u, 1u) }, 0u);
        AssertUpperBound(ByteMultiArraySerializer.Instance, new ByteMultiArray(layout, new byte[] { 1, 2, 3, 4, 5 }));
    }
}
