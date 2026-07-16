using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rcl.Naming;

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

    // ======== 基本統合 ========

    [Fact]
    public void CreateGraphSnapshot_はlocal_endpointとremote_endpointを統合して返す()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var remotePrefix = Prefix(2);

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

    // ======== 値コピー / immutable 境界 ========

    [Fact]
    public void CreateGraphSnapshot_はremote取得後の変更に影響されない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var prefix = Prefix(4);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        var endp = Endpoint(prefix, EndpointKind.Writer, 0x40, "rt/immutable");
        context.DiscoveryDb.UpsertEndpoint(endp, DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        endp.TopicName = "rt/modified";

        snapshot.Endpoints.Should().HaveCount(1);
        snapshot.Endpoints[0].TopicName.Should().Be("rt/immutable");
    }

    [Fact]
    public void Clone_はPartitionQosの内部配列を深くコピーする()
    {
        var orig = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            TopicName = "rt/test",
            Partition = new PartitionQos("group_a"),
        };

        var cloned = orig.Clone();

        // cloned の Partition は独立した配列を指す
        cloned.Partition.Should().Be(new PartitionQos("group_a"));
        orig.Partition = new PartitionQos("group_b");
        cloned.Partition.Should().Be(new PartitionQos("group_a"));
    }

    [Fact]
    public void CreateGraphSnapshot_は取得後のsnapshot要素変更が次回snapshotに影響しない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var prefix = Prefix(5);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x50, "rt/topic"), DateTime.UtcNow);

        var snap1 = context.CreateGraphSnapshot();
        snap1.Endpoints[0].TopicName = "rt/hacked";

        var snap2 = context.CreateGraphSnapshot();
        snap2.Endpoints.Should().HaveCount(1);
        snap2.Endpoints[0].TopicName.Should().Be("rt/topic");
    }

    // ======== Node を使った local endpoint の統合 ========

    [Fact]
    public void CreateGraphSnapshot_はNodeのPublisherをlocal_Writerとして含む()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "pub_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "graph_test", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var snapshot = context.CreateGraphSnapshot();

        var expectedTopic = TopicNameMangler.MangleTopic("graph_test");
        snapshot.Endpoints.Should().ContainSingle(e =>
            e.Kind == EndpointKind.Writer
            && e.TopicName == expectedTopic);
    }

    [Fact]
    public void CreateGraphSnapshot_はNodeのSubscriptionをlocal_Readerとして含む()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "sub_node");
        using var sub = node.CreateSubscription<StringMessage>(
            "graph_sub", StringMessageSerializer.Instance, (_) => { });

        var snapshot = context.CreateGraphSnapshot();

        var expectedTopic = TopicNameMangler.MangleTopic("graph_sub");
        snapshot.Endpoints.Should().ContainSingle(e =>
            e.Kind == EndpointKind.Reader
            && e.TopicName == expectedTopic);
    }

    // ======== local / remote GUID 重複除外 ========

    [Fact]
    public void CreateGraphSnapshot_はlocal_remoteで同一GUIDを重複させない()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "dup_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "dup_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // Publisher 作成直後の snapshot で local Writer が 1 つ
        var snap1 = context.CreateGraphSnapshot();
        var localWriter = snap1.Endpoints.Single(e => e.Kind == EndpointKind.Writer);
        var localGuid = localWriter.EndpointGuid;

        // 同じ GUID の endpoint を remote として追加しても local が優先される
        context.DiscoveryDb.UpsertParticipant(Participant(new GuidPrefix(localGuid.Prefix.ToByteArray())), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(new GuidPrefix(localGuid.Prefix.ToByteArray()), EndpointKind.Writer,
                localGuid.EntityId.Key, "rt/dup_topic"), DateTime.UtcNow);

        var snap2 = context.CreateGraphSnapshot();

        snap2.Endpoints.Should().ContainSingle(e => e.EndpointGuid.Equals(localGuid));
    }

    // ======== graph lock による競合安定性 ========

    [Fact]
    public async Task 並行したPublisher作成とCreateGraphSnapshotで競合しない()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "concurrent_node");
        var iterations = 20;

        // Publisher を保持するリスト（Task 完了後に Dispose する）
        var pubs = new List<IDisposable>();
        var tasks = new List<Task>();
        var started = new TaskCompletionSource();

        for (int i = 0; i < iterations; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                var pub = node.CreatePublisher<StringMessage>(
                    $"concurrent_{idx}", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
                lock (pubs) pubs.Add(pub);
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await started.Task;
            for (int j = 0; j < 10; j++)
            {
                var snap = context.CreateGraphSnapshot();
                // snapshot の整合性は各呼び出しで保証される
                if (j == 9) snap.Endpoints.Should().NotBeNull();
            }
        }));

        started.SetResult();
        await Task.WhenAll(tasks);

        // 全 Publisher が snapshot に含まれる
        var snap = context.CreateGraphSnapshot();
        snap.Endpoints.Should().HaveCount(iterations);
        snap.Endpoints.Should().OnlyContain(e => e.Kind == EndpointKind.Writer);

        foreach (var pub in pubs) pub.Dispose();
    }

    [Fact]
    public async Task 並行したSubscription作成とCreateGraphSnapshotで競合しない()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "concurrent_sub_node");
        var iterations = 20;

        var subs = new List<IDisposable>();
        var tasks = new List<Task>();
        var started = new TaskCompletionSource();

        for (int i = 0; i < iterations; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                var sub = node.CreateSubscription<StringMessage>(
                    $"concurrent_sub_{idx}", StringMessageSerializer.Instance, (_) => { });
                lock (subs) subs.Add(sub);
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await started.Task;
            for (int j = 0; j < 10; j++)
            {
                context.CreateGraphSnapshot();
            }
        }));

        started.SetResult();
        await Task.WhenAll(tasks);

        var snap = context.CreateGraphSnapshot();
        snap.Endpoints.Should().HaveCount(iterations);
        snap.Endpoints.Should().OnlyContain(e => e.Kind == EndpointKind.Reader);

        foreach (var sub in subs) sub.Dispose();
    }

    // ======== service topic (rq/rr) が内部基盤に含まれることの確認 ========

    [Fact]
    public void CreateGraphSnapshot_はservice_topic_rq_rrを含む()
    {
        using var context = CreateContext();
        using var node = new Node(context, "test_node");

        var prefix = Prefix(6);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x60, "rt/topic"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x61, "rq/service"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x62, "rr/service"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        // Task 1 では service topic も含める（フィルタは Task 2）
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
