using Rclsharp.Common;
using Rclsharp.Discovery;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Tests.Discovery;

public class DiscoveryDbTests
{
    private static GuidPrefix Prefix(byte id)
        => GuidPrefix.Create(VendorId.Rclsharp, id, (uint)(0x1000 + id), (ushort)(0x2000 + id));

    private static ParticipantData Participant(GuidPrefix prefix, double leaseSeconds = 20)
        => new()
        {
            Guid = new Guid(prefix, EntityId.Participant),
            LeaseDuration = Duration.FromSeconds(leaseSeconds),
        };

    private static DiscoveredEndpointData Endpoint(
        GuidPrefix prefix,
        EndpointKind kind,
        uint entityKey,
        string topic = "rt/chatter",
        string type = "std_msgs::msg::dds_::String_")
        => new()
        {
            Kind = kind,
            EndpointGuid = new Guid(
                prefix,
                new EntityId(
                    entityKey,
                    kind == EndpointKind.Writer
                        ? EntityKind.UserDefinedWriterNoKey
                        : EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = type,
        };

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

    [Fact]
    public void participant上限到達後の新規participantは保持せず既存更新は許可する()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteParticipants: 1));
        var now = DateTime.UtcNow;
        var first = Participant(Prefix(1));
        var second = Participant(Prefix(2));

        db.UpsertParticipant(first, now);
        db.UpsertParticipant(second, now);
        db.Count.Should().Be(1);
        db.Snapshot()[0].Guid.Should().Be(first.Guid);

        first.EntityName = "updated";
        db.UpsertParticipant(first, now.AddSeconds(1));

        db.Count.Should().Be(1);
        db.Snapshot()[0].Data.EntityName.Should().Be("updated");
    }

    [Fact]
    public void 未知participantに属するendpointは拒否する()
    {
        var db = new DiscoveryDb();

        db.UpsertEndpoint(Endpoint(Prefix(1), EndpointKind.Writer, 0x10), DateTime.UtcNow);

        db.WriterCount.Should().Be(0);
    }

    [Fact]
    public void participant_guidとendpoint_guidのprefixが不一致ならendpointを拒否する()
    {
        var db = new DiscoveryDb();
        var participantPrefix = Prefix(1);
        var endpointPrefix = Prefix(2);
        db.UpsertParticipant(Participant(participantPrefix), DateTime.UtcNow);
        var endpoint = Endpoint(endpointPrefix, EndpointKind.Writer, 0x10);
        endpoint.ParticipantGuid = new Guid(participantPrefix, EntityId.Participant);

        db.UpsertEndpoint(endpoint, DateTime.UtcNow);

        db.WriterCount.Should().Be(0);
    }

    [Fact]
    public void writer上限到達後の新規writerは保持しない()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteWriters: 1));
        var now = DateTime.UtcNow;
        var firstPrefix = Prefix(1);
        var secondPrefix = Prefix(2);
        db.UpsertParticipant(Participant(firstPrefix), now);
        db.UpsertParticipant(Participant(secondPrefix), now);

        db.UpsertEndpoint(Endpoint(firstPrefix, EndpointKind.Writer, 0x10), now);
        db.UpsertEndpoint(Endpoint(secondPrefix, EndpointKind.Writer, 0x11), now);

        db.WriterCount.Should().Be(1);
    }

    [Fact]
    public void participantあたりのendpoint上限到達後は新規endpointを保持しない()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteEndpointsPerParticipant: 1));
        var now = DateTime.UtcNow;
        var prefix = Prefix(1);
        db.UpsertParticipant(Participant(prefix), now);

        db.UpsertEndpoint(Endpoint(prefix, EndpointKind.Writer, 0x10), now);
        db.UpsertEndpoint(Endpoint(prefix, EndpointKind.Reader, 0x11), now);

        db.WriterCount.Should().Be(1);
        db.ReaderCount.Should().Be(0);
    }

    [Fact]
    public void remote_participant_lease_durationは保持前にclampされる()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(
            minRemoteParticipantLeaseSeconds: 1,
            maxRemoteParticipantLeaseSeconds: 2));
        var now = DateTime.UtcNow;
        var prefix = Prefix(1);

        db.UpsertParticipant(Participant(prefix, leaseSeconds: 100), now);

        db.Snapshot()[0].Data.LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        db.UpsertParticipant(Participant(prefix, leaseSeconds: 0.1), now.AddSeconds(1));

        db.Snapshot()[0].Data.LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
    }
}
