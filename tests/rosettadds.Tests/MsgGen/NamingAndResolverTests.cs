using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.Tests.MsgGen;

public class NamingAndResolverTests
{
    [Theory]
    [InlineData("frame_id", "FrameId")]
    [InlineData("data_offset", "DataOffset")]
    [InlineData("ColorRGBA", "ColorRgba")]
    [InlineData("MultiArrayLayout", "MultiArrayLayout")]
    [InlineData("nanosec", "Nanosec")]
    public void ToPascalCase(string input, string expected)
    {
        NamingConventions.ToPascalCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("frame_id", "frameId")]
    [InlineData("r", "r")]
    [InlineData("string", "@string")] // C# 予約語は退避
    public void ToCamelCase(string input, string expected)
    {
        NamingConventions.ToCamelCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("std_msgs", "String", "StringMessage")]
    [InlineData("std_msgs", "Int32", "Int32Message")]
    [InlineData("std_msgs", "ColorRGBA", "ColorRgba")]
    [InlineData("std_msgs", "Header", "Header")]
    [InlineData("std_msgs", "Float32MultiArray", "Float32MultiArray")]
    [InlineData("builtin_interfaces", "Time", "Time")]
    public void CSharpTypeName(string pkg, string ros, string expected)
    {
        new TypeNameResolver().CSharpTypeName(pkg, ros).Should().Be(expected);
    }

    [Theory]
    [InlineData("std_msgs", "ROSettaDDS.Msgs.Std")]
    [InlineData("builtin_interfaces", "ROSettaDDS.Msgs.BuiltinInterfaces")]
    [InlineData("geometry_msgs", "ROSettaDDS.Msgs.Geometry")]
    public void Namespace(string pkg, string expected)
    {
        new TypeNameResolver().Namespace(pkg).Should().Be(expected);
    }

    [Fact]
    public void DdsTypeName()
    {
        var r = new TypeNameResolver();
        r.DdsTypeName("std_msgs", "String").Should().Be("std_msgs::msg::dds_::String_");
        r.RosTypeName("std_msgs", "String").Should().Be("std_msgs/msg/String");
    }
}
