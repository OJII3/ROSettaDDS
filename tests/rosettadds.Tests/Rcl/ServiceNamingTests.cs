using ROSettaDDS.Rcl.Naming;

namespace ROSettaDDS.Tests.Rcl;

public class ServiceNamingTests
{
    [Theory]
    [InlineData("add_two_ints", "rq/add_two_intsRequest")]
    [InlineData("/add_two_ints", "rq/add_two_intsRequest")]
    [InlineData("/ns/svc", "rq/ns/svcRequest")]
    public void MangleServiceRequest_は_rq_prefix_と_Request_suffix(string input, string expected)
    {
        TopicNameMangler.MangleServiceRequest(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("add_two_ints", "rr/add_two_intsReply")]
    [InlineData("/ns/svc", "rr/ns/svcReply")]
    public void MangleServiceReply_は_rr_prefix_と_Reply_suffix(string input, string expected)
    {
        TopicNameMangler.MangleServiceReply(input).Should().Be(expected);
    }
}
