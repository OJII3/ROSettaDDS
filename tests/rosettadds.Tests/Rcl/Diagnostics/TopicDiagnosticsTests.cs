using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rcl.Diagnostics;
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

        var mutationReachedLock = new ManualResetEventSlim();
        var mutationCompleted = new ManualResetEventSlim();

        var origEnter = context.DiscoveryDb.ExternalLockEnter;
        context.DiscoveryDb.ExternalLockEnter = () =>
        {
            mutationReachedLock.Set();
            origEnter?.Invoke();
        };

        var mutationThread = new Thread(() =>
        {
            context.DiscoveryDb.UpsertEndpoint(
                Endpoint(prefix, EndpointKind.Writer, 0x20, "rt/locked"),
                DateTime.UtcNow);
            mutationCompleted.Set();
        });

        lock (context.GraphLock)
        {
            mutationThread.Start();
            Assert.True(mutationReachedLock.Wait(TimeSpan.FromSeconds(5)),
                "mutation must reach ExternalLockEnter");

            Assert.False(mutationCompleted.Wait(TimeSpan.FromMilliseconds(200)),
                "mutation must be blocked by GraphLock");
        }

        Assert.True(mutationCompleted.Wait(TimeSpan.FromSeconds(5)),
            "mutation must complete after GraphLock released");

        context.CreateGraphSnapshot().Endpoints.Should().Contain(e => e.TopicName == "rt/locked");
        context.DiscoveryDb.ExternalLockEnter = origEnter;
    }

    [Fact]
    public void CreateGraphSnapshot中はmutationが_graphLock待ちでブロックされる()
    {
        using var context = CreateContext();
        using var node = new Node(context, "snapshot_blocks_mutation");

        var prefix = Prefix(21);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);

        var snapshotInLock = new ManualResetEventSlim();
        var snapshotContinue = new ManualResetEventSlim();
        context.GraphSnapshotEnterLockCallback = () => snapshotInLock.Set();
        context.GraphSnapshotPauseCallback = () =>
        {
            if (!snapshotContinue.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("snapshot pause timed out");
        };

        var mutationReachedLock = new ManualResetEventSlim();
        var mutationCompleted = new ManualResetEventSlim();

        var origEnter = context.DiscoveryDb.ExternalLockEnter;
        context.DiscoveryDb.ExternalLockEnter = () =>
        {
            mutationReachedLock.Set();
            origEnter?.Invoke();
        };

        var snapshotDone = new ManualResetEventSlim();
        Exception? snapshotError = null;
        var snapshotThread = new Thread(() =>
        {
            try
            {
                context.CreateGraphSnapshot();
            }
            catch (Exception ex)
            {
                snapshotError = ex;
            }
            finally
            {
                snapshotDone.Set();
            }
        });
        snapshotThread.Start();

        Assert.True(snapshotInLock.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must acquire GraphLock and enter lock callback");

        Exception? mutationError = null;
        var mutationThread = new Thread(() =>
        {
            try
            {
                context.DiscoveryDb.UpsertEndpoint(
                    Endpoint(prefix, EndpointKind.Writer, 0x21, "rt/blocked"),
                    DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }
            finally
            {
                mutationCompleted.Set();
            }
        });
        mutationThread.Start();

        Assert.True(mutationReachedLock.Wait(TimeSpan.FromSeconds(5)),
            "mutation must reach ExternalLockEnter");

        Assert.False(mutationCompleted.Wait(TimeSpan.FromMilliseconds(300)),
            "mutation must be blocked while snapshot holds GraphLock");

        snapshotContinue.Set();
        Assert.True(snapshotDone.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must complete after pause released");
        Assert.True(mutationCompleted.Wait(TimeSpan.FromSeconds(5)),
            "mutation must complete after snapshot releases GraphLock");

        if (snapshotError is not null)
            throw new Exception("snapshot thread failed", snapshotError);

        mutationThread.Join();
        if (mutationError is not null)
            throw new Exception("mutation thread failed", mutationError);

        context.CreateGraphSnapshot().Endpoints.Should().Contain(e => e.TopicName == "rt/blocked");

        context.GraphSnapshotEnterLockCallback = null;
        context.GraphSnapshotPauseCallback = null;
        context.DiscoveryDb.ExternalLockEnter = origEnter;
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

    // ======== Task 2: Topic Diagnostics DTO and API ========

    private static TopicDiagnostics CreateDiagnostics(Node node) => node.CreateTopicDiagnostics();

    [Fact]
    public void CreateTopicDiagnostics_がインスタンスを返す()
    {
        using var context = CreateContext();
        using var node = new Node(context, "diag_node");
        using var diag = CreateDiagnostics(node);
        diag.Should().NotBeNull();
    }

    [Fact]
    public void GetTopics_は空のときに空リストを返す()
    {
        using var context = CreateContext();
        using var node = new Node(context, "empty_node");
        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        topics.Should().BeEmpty();
    }

    [Fact]
    public void GetTopics_はrt_topicのみを含みrq_rrを除外する()
    {
        using var context = CreateContext();
        using var node = new Node(context, "filter_node");

        var prefix = Prefix(30);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/chatter"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x11, "rq/some_service"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x12, "rr/some_service"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        topics.Should().ContainSingle();
        topics[0].TopicName.Should().Be("/chatter");
    }

    [Fact]
    public void GetTopics_はtopic名の先頭_スラッシュを付与する()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "slash_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "foo/bar", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        topics.Should().ContainSingle();
        topics[0].TopicName.Should().Be("/foo/bar");
    }

    [Fact]
    public void GetTopics_は型名をdemangleしてRosTypeNameとして保持する()
    {
        using var context = CreateContext();
        using var node = new Node(context, "type_node");

        var prefix = Prefix(31);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/typed",
                "std_msgs::msg::dds_::String_"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        var info = diag.GetTopicInfo("/typed");
        info.Should().NotBeNull();
        info!.Endpoints.Should().ContainSingle();
        info.Endpoints[0].RosTypeName.Should().Be("std_msgs/msg/String");
        info.Endpoints[0].DdsTypeName.Should().Be("std_msgs::msg::dds_::String_");
    }

    [Fact]
    public void GetTopics_は空DDS型でRosTypeNameがnullになる()
    {
        using var context = CreateContext();
        using var node = new Node(context, "empty_type_node");

        var prefix = Prefix(32);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/empty_type", ""), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/empty_type");
        info.Should().NotBeNull();
        info!.Endpoints.Should().ContainSingle();
        info.Endpoints[0].RosTypeName.Should().BeNull();
        info.Endpoints[0].DdsTypeName.Should().Be("");
    }

    [Fact]
    public void GetTopics_は非ROS型名をそのまま表示する()
    {
        using var context = CreateContext();
        using var node = new Node(context, "nonros_node");

        var prefix = Prefix(33);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/nonros", "Custom::MyType"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/nonros");
        info.Should().NotBeNull();
        info!.Endpoints.Should().ContainSingle();
        // 非ROS形式はdemangleせずそのまま
        info.Endpoints[0].RosTypeName.Should().Be("Custom::MyType");
    }

    [Fact]
    public void GetTopics_は複数型を保持する()
    {
        using var context = CreateContext();
        using var node = new Node(context, "multi_type_node");

        var prefix = Prefix(34);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/multi",
                "std_msgs::msg::dds_::String_"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x11, "rt/multi",
                "std_msgs::msg::dds_::Int32_"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/multi");
        info.Should().NotBeNull();
        info!.RosTypeNames.Should().HaveCount(2);
        info.RosTypeNames.Should().Contain("std_msgs/msg/String");
        info.RosTypeNames.Should().Contain("std_msgs/msg/Int32");
    }

    [Fact]
    public void GetTopics_はtopic名とGUIDのordinal順に並ぶ()
    {
        using var context = CreateContext();
        using var node = new Node(context, "sort_node");

        var prefix = Prefix(35);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x30, "rt/zzz"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/aaa"), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Reader, 0x20, "rt/aaa"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        topics.Should().HaveCount(2);
        topics[0].TopicName.Should().Be("/aaa");
        topics[1].TopicName.Should().Be("/zzz");
    }

    [Fact]
    public void GetTopics_は取得後の内部更新で変化しない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "immutable_node");

        var prefix = Prefix(36);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/first"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var topics1 = diag.GetTopics();
        topics1.Should().ContainSingle(t => t.TopicName == "/first");

        // 後から追加しても前のスナップショットは変わらない
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(prefix, EndpointKind.Writer, 0x11, "rt/second"), DateTime.UtcNow);

        topics1.Should().ContainSingle(t => t.TopicName == "/first");
    }

    [Fact]
    public void GetTopicInfo_は既知topicの詳細を返す()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "detail_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "detail_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/detail_topic");
        info.Should().NotBeNull();
        info!.TopicName.Should().Be("/detail_topic");
    }

    [Fact]
    public void GetTopicInfo_はpublisher_subscriber数を正しく返す()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "count_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "count_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
        using var sub = node.CreateSubscription<StringMessage>(
            "count_topic", StringMessageSerializer.Instance, (_) => { });

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/count_topic");
        info.Should().NotBeNull();
        info!.PublisherCount.Should().Be(1);
        info.SubscriberCount.Should().Be(1);
    }

    [Fact]
    public void GetTopicInfo_はendpointのlocal_remoteを正しく判定する()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "locrem_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "locrem_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var remotePrefix = Prefix(37);
        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Writer, 0x10, "rt/locrem_topic"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/locrem_topic");
        info.Should().NotBeNull();
        info!.Endpoints.Should().HaveCount(2);
        info.Endpoints.Should().ContainSingle(e => e.IsLocal);
        info.Endpoints.Should().ContainSingle(e => !e.IsLocal);
    }

    [Fact]
    public void GetTopicInfo_はendpointのReliability_Durabilityを保持する()
    {
        using var context = CreateContext();
        using var node = new Node(context, "qos_node");

        var prefix = Prefix(38);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        var ep = Endpoint(prefix, EndpointKind.Writer, 0x10, "rt/qos_topic");
        ep.Reliability = ReliabilityQos.Reliable;
        ep.Durability = DurabilityQos.TransientLocal;
        context.DiscoveryDb.UpsertEndpoint(ep, DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var info = diag.GetTopicInfo("/qos_topic");
        info.Should().NotBeNull();
        var endpoint = info!.Endpoints.Should().ContainSingle().Subject;
        endpoint.Reliability.Should().Be(ReliabilityQos.Reliable);
        endpoint.Durability.Should().Be(DurabilityQos.TransientLocal);
    }

    [Fact]
    public void GetTopicInfo_は未発見topicにnullを返す()
    {
        using var context = CreateContext();
        using var node = new Node(context, "null_node");
        using var diag = CreateDiagnostics(node);
        diag.GetTopicInfo("/nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetTopicInfo_はnull引数でArgumentException()
    {
        using var context = CreateContext();
        using var node = new Node(context, "arg_node");
        using var diag = CreateDiagnostics(node);
        var act = () => diag.GetTopicInfo(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetTopicInfo_は空文字引数でArgumentException()
    {
        using var context = CreateContext();
        using var node = new Node(context, "arg_node2");
        using var diag = CreateDiagnostics(node);
        var act = () => diag.GetTopicInfo("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose後のGetTopicsはObjectDisposedExceptionを投げる()
    {
        using var context = CreateContext();
        using var node = new Node(context, "disp_node");
        var diag = CreateDiagnostics(node);
        diag.Dispose();
        var act = () => diag.GetTopics();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose後のGetTopicInfoはObjectDisposedExceptionを投げる()
    {
        using var context = CreateContext();
        using var node = new Node(context, "disp_node2");
        var diag = CreateDiagnostics(node);
        diag.Dispose();
        var act = () => diag.GetTopicInfo("/test");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void CreateTopicDiagnostics_はNode_Dispose後はObjectDisposedException()
    {
        using var context = CreateContext();
        var node = new Node(context, "predisp_node");
        node.Dispose();
        var act = () => node.CreateTopicDiagnostics();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TopicInfo_はimmutableなDTOである()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "immut_dto");
        using var pub = node.CreatePublisher<StringMessage>(
            "immut_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        var info = topics[0];

        // TopicInfo は sealed class で setter を持たない
        info.TopicName.Should().Be("/immut_topic");
        info.PublisherCount.Should().Be(1);
        info.SubscriberCount.Should().Be(0);
        info.RosTypeNames.Should().HaveCount(1);
        info.Endpoints.Should().HaveCount(1);
    }

    [Fact]
    public void GetTopics_はlocalとremoteを同一topicに集約する()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "agg_node");
        using var pub = node.CreatePublisher<StringMessage>(
            "agg_topic", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var remotePrefix = Prefix(39);
        context.DiscoveryDb.UpsertParticipant(Participant(remotePrefix), DateTime.UtcNow);
        context.DiscoveryDb.UpsertEndpoint(
            Endpoint(remotePrefix, EndpointKind.Reader, 0x10, "rt/agg_topic"), DateTime.UtcNow);

        using var diag = CreateDiagnostics(node);
        var topics = diag.GetTopics();
        topics.Should().ContainSingle(t => t.TopicName == "/agg_topic");
        var info = diag.GetTopicInfo("/agg_topic");
        info.Should().NotBeNull();
        info!.Endpoints.Should().HaveCount(2);
        info.PublisherCount.Should().Be(1);
        info.SubscriberCount.Should().Be(1);
    }
}
