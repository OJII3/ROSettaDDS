using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rcl.Diagnostics;

public class TopicDiagnosticsTests
{
    private static Context CreateContext()
    {
        var options = new ContextOptions
        {
            Logger = NullLogger.Instance,
            LocalhostOnly = true,
            EnableAutomaticNetworkRecovery = false,
        };
        return new Context(options);
    }

    private static GuidPrefix Prefix(byte id)
        => GuidPrefix.Create(VendorId.ROSettaDDS, id, (uint)(0x1000 + id), (ushort)(0x2000 + id));

    private static ParticipantData Participant(GuidPrefix prefix)
        => new()
        {
            Guid = new Guid(prefix, EntityId.Participant),
            LeaseDuration = Duration.Infinite,
        };

    private static DiscoveredEndpointData Endpoint(
        GuidPrefix prefix,
        EndpointKind kind,
        uint entityKey,
        string topic,
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
    public void CreateGraphSnapshot_はlocal_endpointとremote_endpointを統合して返す()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var localPrefix = context.GuidPrefix;
        var remotePrefix = Prefix(2);

        // remote participant
        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/chatter"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Reader, 0x11, "rt/chatter"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        snapshot.Endpoints.Should().HaveCount(2);
        snapshot.Endpoints.Should().OnlyContain(e => e.TopicName == "rt/chatter");
    }

    [Fact]
    public void CreateGraphSnapshot_は同一GUIDを重複させない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var remotePrefix = Prefix(2);

        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/dup"), DateTime.UtcNow);

        // Remove and re-add the same endpoint GUID — snapshot should reflect only latest state
        context.DiscoveryDb.TryRemoveEndpoint(
            EndpointKind.Writer,
            new Guid(remotePrefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey)));
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/dup"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        snapshot.Endpoints.Should().HaveCount(1);
    }

    [Fact]
    public void CreateGraphSnapshot_はtopic名とGUIDでordinal順に並ぶ()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var remotePrefix = Prefix(3);

        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);

        // Add 3 endpoints with different topic names and GUIDs
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x30, "rt/zzz"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/aaa"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Reader, 0x20, "rt/aaa"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        snapshot.Endpoints.Should().HaveCount(3);
        snapshot.Endpoints[0].TopicName.Should().Be("rt/aaa");
        snapshot.Endpoints[0].EndpointGuid.EntityId.Value.Should().Be(
            new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey).Value);
        snapshot.Endpoints[1].TopicName.Should().Be("rt/aaa");
        snapshot.Endpoints[1].EndpointGuid.EntityId.Value.Should().Be(
            new EntityId(0x20u, EntityKind.UserDefinedReaderNoKey).Value);
        snapshot.Endpoints[2].TopicName.Should().Be("rt/zzz");
    }

    [Fact]
    public void CreateGraphSnapshot_は取得後の変更に影響されない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var prefix = Prefix(4);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        var endp = Endpoint(prefix, EndpointKind.Writer, 0x40, "rt/immutable");
        context.DiscoveryDb.UpsertEndpoint(endp, DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        // Modify the original data
        endp.TopicName = "rt/modified";

        snapshot.Endpoints.Should().HaveCount(1);
        snapshot.Endpoints[0].TopicName.Should().Be("rt/immutable");
    }

    [Fact]
    public void CreateGraphSnapshot_はservice_topic_rq_rrを含まない場合は空ではない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var prefix = Prefix(5);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x50, "rt/topic"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x51, "rq/service"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x52, "rr/service"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        // Note: Task 1 snapshot includes all topics (service filter is Task 2 concern)
        snapshot.Endpoints.Should().HaveCount(3);
    }

    [Fact]
    public void Dispose後のCreateGraphSnapshotはObjectDisposedExceptionを投げる()
    {
        var context = CreateContext();
        context.Dispose();

        var act = () => context.CreateGraphSnapshot();
        act.Should().Throw<ObjectDisposedException>();
    }
}
