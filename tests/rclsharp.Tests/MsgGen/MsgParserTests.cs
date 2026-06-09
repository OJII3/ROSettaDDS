using Rclsharp.MsgGen.Model;
using Rclsharp.MsgGen.Parsing;

namespace Rclsharp.Tests.MsgGen;

public class MsgParserTests
{
    [Fact]
    public void プリミティブとstringのフィールドを解析する()
    {
        var def = MsgParser.Parse("std_msgs", "Header", "builtin_interfaces/Time stamp\nstring frame_id\n");

        def.Package.Should().Be("std_msgs");
        def.Name.Should().Be("Header");
        def.RosTypeName.Should().Be("std_msgs/msg/Header");
        def.Fields.Should().HaveCount(2);

        def.Fields[0].Type.Category.Should().Be(BaseTypeCategory.Named);
        def.Fields[0].Type.Package.Should().Be("builtin_interfaces");
        def.Fields[0].Type.Name.Should().Be("Time");
        def.Fields[0].Name.Should().Be("stamp");

        def.Fields[1].Type.Category.Should().Be(BaseTypeCategory.String);
        def.Fields[1].Name.Should().Be("frame_id");
    }

    [Fact]
    public void コメントと空行を無視する()
    {
        var def = MsgParser.Parse("std_msgs", "X", "# comment\n\nint32 a  # trailing\n");
        def.Fields.Should().HaveCount(1);
        def.Fields[0].Name.Should().Be("a");
        def.Fields[0].Type.PrimitiveName.Should().Be("int32");
    }

    [Theory]
    [InlineData("int32[] a", ArrayKind.Unbounded, 0)]
    [InlineData("int32[5] a", ArrayKind.FixedSize, 5)]
    [InlineData("int32[<=8] a", ArrayKind.Bounded, 8)]
    public void 配列記法を解析する(string line, ArrayKind kind, int len)
    {
        var f = MsgParser.Parse("p", "M", line + "\n").Fields[0];
        f.Type.ArrayKind.Should().Be(kind);
        f.Type.ArrayLength.Should().Be(len);
    }

    [Fact]
    public void bounded_stringを解析する()
    {
        var f = MsgParser.Parse("p", "M", "string<=10 s\n").Fields[0];
        f.Type.Category.Should().Be(BaseTypeCategory.String);
        f.Type.StringBound.Should().Be(10);
    }

    [Fact]
    public void 相対参照のネスト型は同一パッケージ扱い()
    {
        var f = MsgParser.Parse("std_msgs", "MultiArrayLayout", "MultiArrayDimension[] dim\n").Fields[0];
        f.Type.Category.Should().Be(BaseTypeCategory.Named);
        f.Type.Package.Should().BeNull();
        f.Type.Name.Should().Be("MultiArrayDimension");
        f.Type.ArrayKind.Should().Be(ArrayKind.Unbounded);
    }

    [Fact]
    public void 定数を解析する()
    {
        var def = MsgParser.Parse("p", "M", "int32 X=42\nuint8 Y = 7\n");
        def.Constants.Should().HaveCount(2);
        def.Fields.Should().BeEmpty();
        def.Constants[0].Name.Should().Be("X");
        def.Constants[0].Value.Should().Be("42");
        def.Constants[1].Name.Should().Be("Y");
        def.Constants[1].Value.Should().Be("7");
    }

    [Fact]
    public void デフォルト値を解析する()
    {
        var def = MsgParser.Parse("p", "M", "int32 a 5\nstring s \"hi\"\n");
        def.Fields[0].DefaultValue.Should().Be("5");
        def.Fields[1].DefaultValue.Should().Be("\"hi\"");
    }

    [Fact]
    public void 空のmsgはフィールドなし()
    {
        var def = MsgParser.Parse("std_msgs", "Empty", "");
        def.Fields.Should().BeEmpty();
        def.Constants.Should().BeEmpty();
    }

    [Theory]
    [InlineData("wstring w")]
    [InlineData("wstring<=4 w")]
    [InlineData("wstring[] w")]
    public void wstringは未対応として弾く(string line)
    {
        Action act = () => MsgParser.Parse("p", "M", line + "\n");
        act.Should().Throw<MsgParseException>().WithMessage("*wstring*");
    }
}
