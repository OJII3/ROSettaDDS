using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rcl.Naming;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;
using Guid = ROSettaDDS.Common.Guid;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class NodeTests
{
    [Fact]
    public void コンストラクタは_Context_参照と_Name_を保持する()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        using var node = new Node(ctx, "chatter_talker");

        Assert.Same(ctx, node.Context);
        Assert.Equal("chatter_talker", node.Name);
    }

    [Fact]
    public void null_context_を渡すと_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Node(null!, "name"));
    }

    [Fact]
    public void null_name_を渡すと_ArgumentException()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        Assert.Throws<ArgumentException>(() => new Node(ctx, null!));
    }

    [Fact]
    public void CreatePublisher_が_Publisher_を返し_Dispose_後に例外を投げる()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "talker");
        try
        {
            using var pub = node.CreatePublisher<StringMessage>(
                "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
            Assert.NotNull(pub);
        }
        finally { node.Dispose(); }

        Assert.Throws<ObjectDisposedException>(() =>
            node.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
    }

    [Fact]
    public void CreateSubscription_が_Subscription_を返す()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "listener");
        using var sub = node.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg) => { });
        Assert.NotNull(sub);
    }

    [Fact]
    public async Task 異なる_Context_上の_2_Node_で_Pub_Sub_できる()
    {
        var opts = new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
        };
        using var talkerCtx = new Context(opts);
        using var listenerCtx = new Context(opts);
        talkerCtx.Start();
        listenerCtx.Start();

        using var talker = new Node(talkerCtx, "talker");
        using var listener = new Node(listenerCtx, "listener");

        var topicName = $"chatter_{System.Guid.NewGuid():N}";
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var sub = listener.CreateSubscription<StringMessage>(
            topicName, StringMessageSerializer.Instance,
            (msg, _) => received.Enqueue(msg.Data),
            typeName: StringMessage.DdsTypeName);
        using var pub = talker.CreatePublisher<StringMessage>(
            topicName, StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // 両側でマッチしてから配信
        await sub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(10));

        await pub.PublishAsync(new StringMessage("hello"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.False(received.IsEmpty);
        Assert.Equal("hello", received.First());
    }

    [Fact]
    public void CreatePublisher_Phase1後_Disposeが割り込むとrollbackして待機完了する()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        var node = new Node(ctx, "talker");

        var afterPhase1 = new ManualResetEventSlim();
        var disposeFlagSet = new ManualResetEventSlim();
        Exception? createError = null;

        node.BeforeDisposedCheckCallback = () =>
        {
            afterPhase1.Set();
            disposeFlagSet.Wait();
        };

        var createThread = new Thread(() =>
        {
            try
            {
                node.CreatePublisher<StringMessage>(
                    "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
            }
            catch (Exception ex)
            {
                createError = ex;
            }
        });
        createThread.Start();

        Assert.True(afterPhase1.Wait(TimeSpan.FromSeconds(5)),
            "phase 1 (metadata registration) must complete");

        // Simulate Dispose racing in after phase 1, before disposed check
        node.ForceDisposeFlag();
        disposeFlagSet.Set();

        Assert.True(createThread.Join(TimeSpan.FromSeconds(5)),
            "CreatePublisher thread must complete after rollback");

        Assert.NotNull(createError);
        var odEx = Assert.IsType<ObjectDisposedException>(createError);
        Assert.Contains(typeof(Node).Name, odEx.ObjectName, StringComparison.Ordinal);

        // Verify Dispose completes cleanly (pendingRegistrations was decremented)
        node.Dispose();
        Assert.True(node.IsDisposed);

        node.BeforeDisposedCheckCallback = null;
    }

    [Fact]
    public void CreatePublisher_phase2_match_failure_rolls_back()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "pub_test");

        using var sub = node.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance, _ => { });

        var reader = node.Snapshot().Readers[0];
        reader.Dispose();

        var ex = Assert.Throws<ObjectDisposedException>(() =>
            node.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance));

        var snapshot = node.Snapshot();
        snapshot.Writers.Should().BeEmpty();
        snapshot.Readers.Should().Contain(reader);
        reader.MatchedWriterCount.Should().Be(0, "failed writer must be unmatched from existing reader");

        using var pub2 = node.CreatePublisher<StringMessage>(
            "other", StringMessageSerializer.Instance);
        Assert.NotNull(pub2);
    }

    [Fact]
    public void CreateSubscription_phase2_match_failure_rolls_back()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "sub_test");

        using var pub = node.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        var writer = node.Snapshot().Writers[0];
        writer.Dispose();

        var ex = Assert.Throws<ObjectDisposedException>(() =>
            node.CreateSubscription<StringMessage>("chatter", StringMessageSerializer.Instance, _ => { }));

        var snapshot = node.Snapshot();
        snapshot.Readers.Should().BeEmpty();
        snapshot.Writers.Should().Contain(writer);
        writer.MatchedReaderCount.Should().Be(0, "failed reader must be unmatched from existing writer");

        using var sub2 = node.CreateSubscription<StringMessage>(
            "other", StringMessageSerializer.Instance, _ => { });
        Assert.NotNull(sub2);
    }

    [Fact]
    public void CreateServiceClient_reply_reader_phase2_match_failure_rolls_back()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "svc_test");

        var svcName = $"test_{System.Guid.NewGuid():N}";
        var replyTopic = TopicNameMangler.MangleServiceReply(svcName);

        var writerGuid = new Guid(ctx.GuidPrefix, ctx.UserEntityIds.AllocateWriter());
        var transport = new SilentTransport();
        var history = new WriterHistoryCache(writerGuid);
        var replyWriter = new StatefulWriter(
            transport,
            Locator.FromUdpV4(IPAddress.Loopback, 7401),
            ProtocolVersion.Current,
            VendorId.ROSettaDDS,
            ctx.GuidPrefix,
            writerGuid.EntityId,
            TimeSpan.FromSeconds(1),
            history,
            NullLogger.Instance);

        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = ctx.Guid,
            TopicName = replyTopic,
            TypeName = "std_msgs::msg::dds_::String_",
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7411));

        node.RegisterWriterMetadataForTest(endpointData, replyWriter);
        replyWriter.Dispose();

        var descriptor = new ServiceDescriptor<StringMessage, StringMessage>(
            requestDdsTypeName: "std_msgs::msg::dds_::String_",
            responseDdsTypeName: "std_msgs::msg::dds_::String_",
            requestSerializer: StringMessageSerializer.Instance,
            responseSerializer: StringMessageSerializer.Instance);

        var ex = Assert.Throws<ObjectDisposedException>(() =>
            node.CreateServiceClient(descriptor, svcName));

        var snapshot = node.Snapshot();
        snapshot.Readers.Should().BeEmpty();
        snapshot.Writers.Should().HaveCount(1,
            "manually injected reply writer must remain; request publisher must be rolled back");

        using var sub2 = node.CreateSubscription<StringMessage>(
            "other", StringMessageSerializer.Instance, _ => { });
        Assert.NotNull(sub2);
    }

    private sealed class SilentTransport : IRtpsTransport
    {
        public Locator LocalLocator => Locator.FromUdpV4(IPAddress.Loopback, 7411);
        public event Action<ReadOnlyMemory<byte>, Locator>? Received { add { } remove { } }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default) => default;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
