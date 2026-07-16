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

    // ======== local + remote 統合 (同一 snapshot 内) ========

    [Fact]
    public void CreateGraphSnapshot_はlocalとremoteの双方を同一snapshotに含める()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "both_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "both_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var remotePrefix = Prefix(10);
        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/both_topic"), DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();

        var expectedTopic = TopicNameMangler.MangleTopic("both_topic");
        snapshot.Endpoints.Should().Contain(e =>
            e.Kind == EndpointKind.Writer
            && e.TopicName == expectedTopic);
        snapshot.Endpoints.Should().Contain(e =>
            e.Kind == EndpointKind.Writer
            && e.TopicName == "rt/both_topic");
        // local + remote = 2 endpoints (same topic, different GUIDs)
        snapshot.Endpoints.Should().HaveCount(2);
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

        cloned.Partition.Should().Be(new PartitionQos("group_a"));
        orig.Partition = new PartitionQos("group_b");
        cloned.Partition.Should().Be(new PartitionQos("group_a"));
    }

    [Fact]
    public void Clone_はPartitionQosの元配列要素変更に影響されない()
    {
        var names = new[] { "original" };
        var orig = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            TopicName = "rt/array_test",
            Partition = new PartitionQos(names),
        };

        var cloned = orig.Clone();

        // 元の配列要素を書き換えても cloned に影響しない
        names[0] = "mutated";
        cloned.Partition.Names.Should().Equal("original");
    }

    [Fact]
    public void CreateGraphSnapshot_はPartitionQosの元配列要素変更に影響されない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "part_arr_node");

        var prefix = Prefix(7);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);

        var names = new[] { "snapshot_group" };
        var endp = Endpoint(prefix, EndpointKind.Writer, 0x70, "rt/part_arr");
        endp.Partition = new PartitionQos(names);
        context.DiscoveryDb.UpsertEndpoint(endp, DateTime.UtcNow);

        // snapshot 取得後に元の配列要素を書き換えても snapshot に影響しない
        var snapshot = context.CreateGraphSnapshot();
        snapshot.Endpoints[0].Partition.Names.Should().Equal("snapshot_group");

        names[0] = "corrupted";
        snapshot.Endpoints[0].Partition.Names.Should().Equal("snapshot_group");
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

    // ======== Dispose 時の SEDP unregister 通知 ========

    [Fact]
    public void Node_Dispose_でSEDP_unregisterが発行される()
    {
        using var context = CreateContext();
        context.Start();
        var initialCount = context.PublishedPublicationStateCount;

        var node = new Node(context, "sedp_node");
        var pub = node.CreatePublisher<StringMessage>(
            "sedp_test", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // 広告後は count が増えている (add publication が発行された)
        var afterAdvertise = context.PublishedPublicationStateCount;
        afterAdvertise.Should().BeGreaterThan(initialCount,
            "add publication must increase PublishedPublicationStateCount");

        node.Dispose();

        // Dispose 後は count が再度増えている (unregister publication が発行された)
        var afterDispose = context.PublishedPublicationStateCount;
        afterDispose.Should().BeGreaterThan(afterAdvertise,
            "dispose must send unregister and increase PublishedPublicationStateCount");
    }

    // ======== graph lock 競合検出 ========

    [Fact]
    public void ExternalLockEnter_は_graphLock_と同一Monitorでmutationをブロックする()
    {
        using var context = CreateContext();
        using var node = new Node(context, "lock_contention");

        var prefix = Prefix(20);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);

        // GraphLock を保持 → ExternalLockEnter がブロックすることを確認
        var mutationStarted = new ManualResetEventSlim();
        var mutationCompleted = false;

        var mutationThread = new Thread(() =>
        {
            mutationStarted.Set();
            // UpsertEndpoint 内部で ExternalLockEnter → Monitor.Enter(_graphLock) を呼ぶ
            context.DiscoveryDb.UpsertEndpoint(
                Endpoint(prefix, EndpointKind.Writer, 0x20, "rt/locked"),
                DateTime.UtcNow);
            mutationCompleted = true;
        });

        lock (context.GraphLock)
        {
            mutationThread.Start();
            mutationStarted.Wait(TimeSpan.FromSeconds(1));

            // Mutation スレッドは Monitor.Enter(_graphLock) でブロックされている
            Thread.Sleep(200);
            mutationCompleted.Should().BeFalse(
                "mutation must be blocked by same lock as GraphLock");

            // Lock 保持中は snapshot もブロックされる → test thread が lock を保持しているので
            // このテストでは snapshot は取らない（別スレッドで取ればブロック確認できる）
        }

        // Lock 解放 → mutation が進行
        mutationThread.Join(TimeSpan.FromSeconds(1));
        mutationCompleted.Should().BeTrue("mutation must complete after GraphLock released");

        // mutation 後の状態を確認
        context.CreateGraphSnapshot().Endpoints.Should().Contain(e => e.TopicName == "rt/locked");
    }

    [Fact]
    public void CreateGraphSnapshot中はmutationが_graphLock待ちでブロックされる()
    {
        using var context = CreateContext();
        using var node = new Node(context, "snapshot_blocks_mutation");

        var prefix = Prefix(21);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);

        // long snapshot をシミュレートする代わりに、
        // GraphLock 保持中に mutation がブロックされることを確認する
        var mutationQueued = false;
        var mutationCompleted = false;

        var mutationThread = new Thread(() =>
        {
            mutationQueued = true;
            context.DiscoveryDb.UpsertEndpoint(
                Endpoint(prefix, EndpointKind.Writer, 0x21, "rt/blocked"),
                DateTime.UtcNow);
            mutationCompleted = true;
        });

        // GraphLock を取得 (CreateGraphSnapshot 内部で取得されるのと同一)
        lock (context.GraphLock)
        {
            mutationThread.Start();
            Thread.Sleep(200);

            mutationCompleted.Should().BeFalse(
                "mutation must be blocked while GraphLock is held (as in CreateGraphSnapshot)");
        }

        mutationThread.Join(TimeSpan.FromSeconds(1));
        mutationCompleted.Should().BeTrue(
            "mutation must complete after GraphLock released");

        context.CreateGraphSnapshot().Endpoints.Should().Contain(e => e.TopicName == "rt/blocked");
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
