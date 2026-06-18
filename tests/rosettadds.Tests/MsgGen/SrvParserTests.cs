using System;
using System.Linq;
using ROSettaDDS.MsgGen.Parsing;

namespace ROSettaDDS.Tests.MsgGen;

public class SrvParserTests
{
    private const string AddTwoInts = "int64 a\nint64 b\n---\nint64 sum\n";

    [Fact]
    public void Parse_は_request_と_response_の_2定義を返す()
    {
        var (request, response) = SrvParser.Parse("example_interfaces", "AddTwoInts", AddTwoInts);

        request.Name.Should().Be("AddTwoInts_Request");
        request.SubNamespace.Should().Be("srv");
        request.RosTypeName.Should().Be("example_interfaces/srv/AddTwoInts_Request");
        request.Fields.Select(f => f.Name).Should().Equal("a", "b");

        response.Name.Should().Be("AddTwoInts_Response");
        response.SubNamespace.Should().Be("srv");
        response.Fields.Select(f => f.Name).Should().Equal("sum");
    }

    [Fact]
    public void Parse_は_空の_request_response_を許容する()
    {
        var (request, response) = SrvParser.Parse("std_srvs", "Empty", "---\n");

        request.Fields.Should().BeEmpty();
        response.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Parse_は_区切りが無いと例外()
    {
        Action act = () => SrvParser.Parse("p", "X", "int64 a\nint64 b\n");

        act.Should().Throw<MsgParseException>();
    }

    [Fact]
    public void ServiceDescriptorEmitter_は_記述子クラスを生成する()
    {
        var (request, response) = SrvParser.Parse("example_interfaces", "AddTwoInts", AddTwoInts);
        var resolver = new ROSettaDDS.MsgGen.TypeMapping.TypeNameResolver();

        string code = new ROSettaDDS.MsgGen.Emitting.ServiceDescriptorEmitter(resolver)
            .Emit("example_interfaces", "AddTwoInts", request, response);

        code.Should().Contain("namespace ROSettaDDS.Msgs.ExampleInterfaces");
        code.Should().Contain("public static class AddTwoIntsService");
        code.Should().Contain("ServiceDescriptor<AddTwoIntsRequest, AddTwoIntsResponse>");
        code.Should().Contain("AddTwoIntsRequestSerializer.Instance");
        code.Should().Contain("AddTwoIntsResponseSerializer.Instance");
        code.Should().Contain("example_interfaces::srv::dds_::AddTwoInts_Request_");
        code.Should().Contain("example_interfaces::srv::dds_::AddTwoInts_Response_");
    }
}
