using Rclsharp.Common;
using Rclsharp.Discovery;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Tests.Discovery;

public class DiscoveryDbTests
{
    [Fact]
    public void ExpireOldParticipants_は同じparticipant_prefixのendpointを削除してLostを通知する()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = GuidPrefix.Create(VendorId.Rclsharp, 0x01, 0x02, 0x03);
        var participantGuid = new Guid(prefix, EntityId.Participant);
        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));
        var readerGuid = new Guid(prefix, new EntityId(0x11u, EntityKind.UserDefinedReaderNoKey));
        var events = new List<string>();

        db.WriterLost += endpoint => events.Add($"writer:{endpoint.Guid}");
        db.ReaderLost += endpoint => events.Add($"reader:{endpoint.Guid}");
        db.ParticipantLost += participant => events.Add($"participant:{participant.Guid}");

        db.UpsertParticipant(new ParticipantData
        {
            Guid = participantGuid,
            LeaseDuration = Duration.FromSeconds(1),
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = readerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);

        db.ExpireOldParticipants(now + TimeSpan.FromSeconds(2));

        db.Count.Should().Be(0);
        db.WriterCount.Should().Be(0);
        db.ReaderCount.Should().Be(0);
        events.Should().Equal(
            $"writer:{writerGuid}",
            $"reader:{readerGuid}",
            $"participant:{participantGuid}");
    }
}
