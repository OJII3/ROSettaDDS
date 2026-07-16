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

        // cloned の Partition は独立した配列を指す
        cloned.Partition.Should().Be(new PartitionQos("group_a"));
        orig.Partition = new PartitionQos("group_b");
        cloned.Partition.Should().Be(new PartitionQos("group_a"));
    }

    [Fact]
    public void Clone_はPartitionQosの配列要素変更がsnapshotに影響しない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "partition_node");

        var prefix = Prefix(7);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        var endp = Endpoint(prefix, EndpointKind.Writer, 0x70, "rt/part_topic");
        endp.Partition = new PartitionQos("group_x");
        context.DiscoveryDb.UpsertEndpoint(endp, DateTime.UtcNow);

        var snapshot = context.CreateGraphSnapshot();
        snapshot.Endpoints[0].Partition.Should().Be(new PartitionQos("group_x"));

        // 元の PartitionQos は値型だが内部配列は独立している
        endp.Partition = new PartitionQos("group_y");
        snapshot.Endpoints[0].Partition.Should().Be(new PartitionQos("group_x"));
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
    public void Node_Dispose_でSEDP_unregisterが呼ばれる()
    {
        using var context = CreateContext();
        context.Start();
        var node = new Node(context, "sedp_node");
        var pub = node.CreatePublisher<StringMessage>(
            "sedp_test", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var snapBefore = context.CreateGraphSnapshot();
        snapBefore.Endpoints.Should().Contain(e => e.Kind == EndpointKind.Writer);

        // Dispose で endpoint が除去される
        node.Dispose();

        var snapAfter = context.CreateGraphSnapshot();
        snapAfter.Endpoints.Should().NotContain(e => e.Kind == EndpointKind.Writer);
    }

    // ======== graph lock による競合安定性 ========

    // --- 内部境界を使った競合テスト (UDP 非依存) ---

    [Fact]
    public async Task 内部境界_並行remote追加とCreateGraphSnapshotで中間値が許容集合()
    {
        using var context = CreateContext();
        using var node = new Node(context, "race_node");

        var prefix = Prefix(20);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);

        var added = new List<Guid>();
        var tasks = new List<Task>();
        var started = new TaskCompletionSource();
        var snapshotResults = new List<GraphSnapshot>();
        const int iterations = 15;

        for (int i = 0; i < iterations; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                var endp = Endpoint(prefix, EndpointKind.Writer, (uint)(0x100 + idx), $"rt/race_{idx}");
                context.DiscoveryDb.UpsertEndpoint(endp, DateTime.UtcNow);
                lock (added) { if (!added.Contains(endp.EndpointGuid)) added.Add(endp.EndpointGuid); }
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await started.Task;
            for (int j = 0; j < 10; j++)
            {
                var snap = context.CreateGraphSnapshot();
                lock (snapshotResults) snapshotResults.Add(snap);
            }
        }));

        started.SetResult();
        await Task.WhenAll(tasks);

        // 全端点が最終的に追加されている
        context.CreateGraphSnapshot().Endpoints.Should().HaveCount(iterations);

        // 各中間 snapshot の GUID 集合は追加前または追加後の一貫した部分集合
        var finalGuids = new HashSet<Guid>(added);
        foreach (var snap in snapshotResults)
        {
            var snapGuids = snap.Endpoints.Select(e => e.EndpointGuid).ToHashSet();
            // snapGuids は finalGuids の部分集合
            snapGuids.IsSubsetOf(finalGuids).Should().BeTrue(
                "snapshot GUID must be subset of final set");
        }
    }

    [Fact]
    public async Task 内部境界_並行remote削除とCreateGraphSnapshotで中間値が許容集合()
    {
        using var context = CreateContext();
        using var node = new Node(context, "delete_race_node");

        var prefix = Prefix(21);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        for (int i = 0; i < 15; i++)
        {
            context.DiscoveryDb.UpsertEndpoint(
                Endpoint(prefix, EndpointKind.Writer, (uint)(0x200 + i), $"rt/del_{i}"),
                DateTime.UtcNow);
        }

        var tasks = new List<Task>();
        var started = new TaskCompletionSource();
        var snapshotResults = new List<GraphSnapshot>();
        var initialCount = context.CreateGraphSnapshot().Endpoints.Count;

        for (int i = 0; i < 15; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                context.DiscoveryDb.TryRemoveEndpoint(
                    EndpointKind.Writer,
                    new Guid(prefix, new EntityId((uint)(0x200 + idx), EntityKind.UserDefinedWriterNoKey)));
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await started.Task;
            for (int j = 0; j < 10; j++)
            {
                var snap = context.CreateGraphSnapshot();
                lock (snapshotResults) snapshotResults.Add(snap);
            }
        }));

        started.SetResult();
        await Task.WhenAll(tasks);

        // 全端点が削除されている
        context.CreateGraphSnapshot().Endpoints.Should().BeEmpty();

        // 各中間 snapshot は初期状態の部分集合
        foreach (var snap in snapshotResults)
        {
            snap.Endpoints.Count.Should().BeLessOrEqualTo(initialCount,
                "intermediate snapshot must be subset of initial state");
        }
    }

    [Fact]
    public async Task 内部境界_並行remote追加_削除とCreateGraphSnapshotで競合しない()
    {
        using var context = CreateContext();
        using var node = new Node(context, "mixed_race_node");

        var prefix = Prefix(22);
        context.DiscoveryDb.UpsertParticipant(Participant(prefix), DateTime.UtcNow);
        for (int i = 0; i < 10; i++)
        {
            context.DiscoveryDb.UpsertEndpoint(
                Endpoint(prefix, EndpointKind.Writer, (uint)(0x300 + i), $"rt/mix_{i}"),
                DateTime.UtcNow);
        }

        var tasks = new List<Task>();
        var started = new TaskCompletionSource();
        var snapshotResults = new List<GraphSnapshot>();
        var initialGuids = context.CreateGraphSnapshot().Endpoints
            .Select(e => e.EndpointGuid).ToHashSet();

        // 5 件削除 + 5 件追加を並行
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                context.DiscoveryDb.TryRemoveEndpoint(
                    EndpointKind.Writer,
                    new Guid(prefix, new EntityId((uint)(0x300 + idx), EntityKind.UserDefinedWriterNoKey)));
            }));
        }
        for (int i = 0; i < 5; i++)
        {
            int idx = i + 100;
            tasks.Add(Task.Run(async () =>
            {
                await started.Task;
                context.DiscoveryDb.UpsertEndpoint(
                    Endpoint(prefix, EndpointKind.Writer, (uint)(0x400 + idx), $"rt/new_{idx}"),
                    DateTime.UtcNow);
            }));
        }

        tasks.Add(Task.Run(async () =>
        {
            await started.Task;
            for (int j = 0; j < 10; j++)
            {
                var snap = context.CreateGraphSnapshot();
                lock (snapshotResults) snapshotResults.Add(snap);
            }
        }));

        started.SetResult();
        await Task.WhenAll(tasks);

        var finalSnap = context.CreateGraphSnapshot();
        // 10 初期 - 5 削除 + 5 追加 = 10
        finalSnap.Endpoints.Should().HaveCount(10);

        // 各中間 snapshot の GUID は初期集合と最終集合の和集合の部分集合
        var finalGuids = finalSnap.Endpoints.Select(e => e.EndpointGuid).ToHashSet();
        var allPossible = new HashSet<Guid>(initialGuids);
        allPossible.UnionWith(finalGuids);

        foreach (var snap in snapshotResults)
        {
            var snapGuids = snap.Endpoints.Select(e => e.EndpointGuid).ToHashSet();
            snapGuids.IsSubsetOf(allPossible).Should().BeTrue(
                "snapshot GUID must be subset of initial ∪ final set");
        }
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
