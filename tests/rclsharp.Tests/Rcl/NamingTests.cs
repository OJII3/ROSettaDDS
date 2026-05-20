using Rclsharp.Common;
using Rclsharp.Rcl.Naming;

namespace Rclsharp.Tests.Rcl;

public class TopicNameManglerTests
{
    [Theory]
    [InlineData("chatter", "rt/chatter")]
    [InlineData("/chatter", "rt/chatter")]
    [InlineData("/foo/bar", "rt/foo/bar")]
    public void MangleTopic_は_先頭スラッシュを除いて_rt_を付ける(string input, string expected)
    {
        TopicNameMangler.MangleTopic(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("rt/chatter", "chatter")]
    [InlineData("rt/foo/bar", "foo/bar")]
    [InlineData("not_rt_prefix", "not_rt_prefix")]
    public void DemangleTopic_は_rt_prefix_を除く(string input, string expected)
    {
        TopicNameMangler.DemangleTopic(input).Should().Be(expected);
    }
}

public class TypeNameManglerTests
{
    [Theory]
    [InlineData("std_msgs/msg/String", "std_msgs::msg::dds_::String_")]
    [InlineData("geometry_msgs/msg/Twist", "geometry_msgs::msg::dds_::Twist_")]
    public void MangleType_は_dds_と末尾アンダースコアを付ける(string input, string expected)
    {
        TypeNameMangler.MangleType(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("std_msgs::msg::dds_::String_", "std_msgs/msg/String")]
    [InlineData("geometry_msgs::msg::dds_::Twist_", "geometry_msgs/msg/Twist")]
    public void DemangleType_は_dds_と末尾アンダースコアを除く(string input, string expected)
    {
        TypeNameMangler.DemangleType(input).Should().Be(expected);
    }

    [Fact]
    public void Mangle_と_Demangle_は_往復()
    {
        var ros = "std_msgs/msg/String";
        TypeNameMangler.DemangleType(TypeNameMangler.MangleType(ros)).Should().Be(ros);
    }
}

public class UserEntityIdAllocatorTests
{
    [Fact]
    public void 最初の_writer_id_は_FastDDS_互換の小さい連番()
    {
        var allocator = new UserEntityIdAllocator();

        var id = allocator.AllocateWriter();

        id.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey));
        id.Value.Should().Be(0x0000_0503u);
    }

    [Fact]
    public void 最初の_reader_id_は_FastDDS_互換の小さい連番()
    {
        var allocator = new UserEntityIdAllocator();

        var id = allocator.AllocateReader();

        id.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedReaderNoKey));
        id.Value.Should().Be(0x0000_0504u);
    }

    [Fact]
    public void writer_と_reader_は別々に連番を進める()
    {
        var allocator = new UserEntityIdAllocator();

        var writer1 = allocator.AllocateWriter();
        var writer2 = allocator.AllocateWriter();
        var reader1 = allocator.AllocateReader();
        var reader2 = allocator.AllocateReader();

        writer1.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey));
        writer2.Should().Be(new EntityId(0x000006u, EntityKind.UserDefinedWriterNoKey));
        reader1.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedReaderNoKey));
        reader2.Should().Be(new EntityId(0x000006u, EntityKind.UserDefinedReaderNoKey));
    }

    [Fact]
    public void key_上限を超えると例外()
    {
        var allocator = new UserEntityIdAllocator(0x00FF_FFFFu);
        allocator.AllocateReader().Key.Should().Be(0x00FF_FFFFu);

        Action act = () => allocator.AllocateReader();

        act.Should().Throw<InvalidOperationException>();
    }
}
