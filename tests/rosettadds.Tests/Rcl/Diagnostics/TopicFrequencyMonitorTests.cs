using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Reader;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rcl.Diagnostics;

public class TopicFrequencyMonitorTests
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

    // ======== RawSubscription 基本動作 ========

    [Fact]
    public void RawSubscription_はraw_payload_callbackを受け取る()
    {
        var reader = new TestUserReader(new EntityId(1, EntityKind.UserDefinedReaderNoKey));
        byte[]? received = null;
        using var raw = new RawSubscription(
            "t", default, reader, (payload, _) => received = payload.ToArray(), autoStart: false);

        reader.SimulatePayload(new byte[] { 1, 2, 3 }, default);

        received.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void RawSubscription_はDispose後にcallbackを発火しない()
    {
        var reader = new TestUserReader(new EntityId(2, EntityKind.UserDefinedReaderNoKey));
        int count = 0;
        var raw = new RawSubscription(
            "t", default, reader, (_, _) => Interlocked.Increment(ref count), autoStart: false);
        raw.Dispose();

        reader.SimulatePayload(ReadOnlyMemory<byte>.Empty, default);

        count.Should().Be(0);
    }

    [Fact]
    public void RawSubscription_は二重Disposeで例外を投げない()
    {
        var reader = new TestUserReader(new EntityId(3, EntityKind.UserDefinedReaderNoKey));
        var raw = new RawSubscription("t", default, reader, (_, _) => { }, autoStart: false);
        raw.Dispose();
        raw.Dispose();
    }

    // ======== 既定 QoS (BestEffort/Volatile) の match 検証 ========

    [Fact]
    public void 既定BestEffortVolatileのreaderは同じQoSのwriterとmatchする()
    {
        var r = new LocalReader(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Reader,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.BestEffort,
                Durability = DurabilityQos.Volatile,
            }, null!);
        var w = new LocalWriter(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Writer,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.BestEffort,
                Durability = DurabilityQos.Volatile,
            }, null!);

        EndpointMatcher.EvaluateLocalLocal(r, w).IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void 既定BestEffortVolatileのreaderはReliableTransientLocal_writerとmatchする()
    {
        var r = new LocalReader(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Reader,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.BestEffort,
                Durability = DurabilityQos.Volatile,
            }, null!);
        var w = new LocalWriter(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Writer,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.Reliable,
                Durability = DurabilityQos.TransientLocal,
            }, null!);

        EndpointMatcher.EvaluateLocalLocal(r, w).IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void 型名不一致でmatchしない()
    {
        var r = new LocalReader(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Reader,
                TopicName = "rt/test",
                TypeName = "type_a",
                Reliability = ReliabilityQos.BestEffort,
            }, null!);
        var w = new LocalWriter(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Writer,
                TopicName = "rt/test",
                TypeName = "type_b",
                Reliability = ReliabilityQos.BestEffort,
            }, null!);

        EndpointMatcher.EvaluateLocalLocal(r, w).IsCompatible.Should().BeFalse();
    }

    [Fact]
    public void 既定QoSのreaderはremote_writerともmatchできる()
    {
        var r = new LocalReader(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Reader,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.BestEffort,
                Durability = DurabilityQos.Volatile,
            }, null!);
        var remote = new RemoteEndpoint(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Writer,
                TopicName = "rt/test",
                TypeName = "test::msg::dds_::Msg_",
                Reliability = ReliabilityQos.Reliable,
                Durability = DurabilityQos.Volatile,
            }, DateTime.UtcNow);

        EndpointMatcher.EvaluateLocalRemote(r, remote).IsCompatible.Should().BeTrue();
    }

    // ======== Node 統合: registration / SEDP / Dispose ========

    [Fact]
    public void CreateRawReader_はSEDPにreaderを広告する()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "raw_node");
        var initialCount = context.PublishedSubscriptionStateCount;

        using var raw = node.CreateRawReader(
            "rt/raw_test",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        context.PublishedSubscriptionStateCount.Should().BeGreaterThan(initialCount);
    }

    [Fact]
    public void CreateRawReader_DisposeでSEDP_unregisterが発行される()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "sedp_node");
        var initialCount = context.PublishedSubscriptionStateCount;

        var raw = node.CreateRawReader(
            "rt/sedp_raw",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        var afterCreate = context.PublishedSubscriptionStateCount;
        afterCreate.Should().BeGreaterThan(initialCount);

        raw.Dispose();

        var afterDispose = context.PublishedSubscriptionStateCount;
        afterDispose.Should().BeGreaterThan(afterCreate,
            "dispose must send SEDP unregister (increment published count)");
    }

    [Fact]
    public void Dispose後のNodeでCreateRawReaderはObjectDisposedException()
    {
        using var context = CreateContext();
        var node = new Node(context, "predisp_node");
        node.Dispose();
        var act = () => node.CreateRawReader(
            "rt/t", "t", (_, _) => { }, ReliabilityQos.BestEffort, DurabilityQos.Volatile);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Context_Dispose後のRawSubscription_Disposeは安全()
    {
        var context = CreateContext();
        context.Start();
        var node = new Node(context, "ctx_first");
        var raw = node.CreateRawReader(
            "rt/ctx_first",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        context.Dispose();

        raw.Dispose();
    }

    [Fact]
    public void SEDP広告前にDisposeしても安全()
    {
        using var context = CreateContext();
        context.Start();
        using var node = new Node(context, "fast_disp");

        var raw = node.CreateRawReader(
            "rt/fast_disp",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        raw.Dispose();
    }

    // ======== RawSubscription の callback 同時実行 ========

    [Fact]
    public void Dispose中のcallback同時実行で破損しない()
    {
        var reader = new TestUserReader(new EntityId(10, EntityKind.UserDefinedReaderNoKey));
        int callCount = 0;
        using var raw = new RawSubscription(
            "t", default, reader, (_, _) => Interlocked.Increment(ref callCount), autoStart: false);

        var barrier = new Barrier(3);
        var threads = new Thread[3];
        for (int i = 0; i < 3; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int j = 0; j < 100; j++)
                    reader.SimulatePayload(new byte[] { (byte)j }, default);
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        callCount.Should().Be(300);
    }

    [Fact]
    public void Disposeとcallback同時実行で破損しない()
    {
        var reader = new TestUserReader(new EntityId(11, EntityKind.UserDefinedReaderNoKey));
        var raw = new RawSubscription(
            "t", default, reader, (_, _) => Thread.SpinWait(100), autoStart: false);

        var callbackThread = new Thread(() =>
        {
            for (int i = 0; i < 50; i++)
                reader.SimulatePayload(new byte[] { (byte)i }, default);
        });
        callbackThread.Start();

        Thread.SpinWait(50);
        raw.Dispose();

        callbackThread.Join();
    }

    // ======== Test helper ========

    private sealed class TestUserReader : IUserReader
    {
        public TestUserReader(EntityId readerEntityId)
        {
            ReaderEntityId = readerEntityId;
            Guid = new Guid(GuidPrefix.Unknown, readerEntityId);
        }

        public EntityId ReaderEntityId { get; }
        public Guid Guid { get; }
        public IRtpsSubmessageHandler Handler =>
            throw new NotSupportedException("TestUserReader does not support Handler");
        public event Action<ReadOnlyMemory<byte>, GuidPrefix>? PayloadReceived;
        public int MatchedWriterCount => 0;
        public SubscriptionMatchedStatus SubscriptionMatchedStatus => default;
        public RtpsReaderDiagnostics Diagnostics =>
            throw new NotSupportedException("TestUserReader does not support Diagnostics");

        public void MatchWriter(Guid writerGuid, Locator? unicastReplyLocator) { }
        public void UnmatchWriter(Guid writerGuid) { }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void SimulatePayload(ReadOnlyMemory<byte> payload, GuidPrefix sourcePrefix)
            => PayloadReceived?.Invoke(payload, sourcePrefix);
    }
}
