using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Reader;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class UserEndpointManagerTests
{
    [Fact]
    public void Writer登録APIへReader_endpointを渡すと拒否する()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var manager = new UserEndpointManager(
            new DiscoveryDb(), new ParticipantRtpsReceiver(prefix), NullLogger.Instance);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, new EntityId(1, EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        };

        var act = () => manager.RegisterWriter(endpoint, null!);

        act.Should().Throw<ArgumentException>().WithParameterName("endpoint");
    }

    [Fact]
    public void Reader登録APIは空topicを拒否する()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var manager = new UserEndpointManager(
            new DiscoveryDb(), new ParticipantRtpsReceiver(prefix), NullLogger.Instance);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, new EntityId(1, EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = "",
            TypeName = "std_msgs::msg::dds_::String_",
        };
        var reader = new BestEffortUserReader(
            prefix,
            endpoint.EndpointGuid.EntityId,
            NullLogger.Instance,
            DataFragReassemblyOptions.Default);

        var act = () => manager.RegisterReader(endpoint, reader);

        act.Should().Throw<ArgumentException>().WithParameterName("endpoint");
    }
}
