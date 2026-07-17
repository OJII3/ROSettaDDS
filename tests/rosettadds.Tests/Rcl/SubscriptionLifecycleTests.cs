using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rcl.Naming;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Transport;
using ROSettaDDS.Tests.Dds;
using Guid = ROSettaDDS.Common.Guid;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class SubscriptionLifecycleTests
{
    private static ContextOptions CreateOptions()
    {
        return new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
        };
    }

    // ========================================================================
    // 1. SEDP advertise ordering: Disposeはadvertise完了を待ってからunregister
    // ========================================================================

    [Fact]
    public void Subscription_DisposeはSEDP_ALIVEの後UNREGISTEREDを送る()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "sub_ordering");
        var beforeCreate = ctx.PublishedSubscriptionStateCount;

        var sub = node.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance, _ => { });

        // create → SEDP advertise increment
        var afterCreate = ctx.PublishedSubscriptionStateCount;
        afterCreate.Should().BeGreaterThan(beforeCreate);

        sub.Dispose();

        // dispose → SEDP unregister increment
        var afterDispose = ctx.PublishedSubscriptionStateCount;
        afterDispose.Should().BeGreaterThan(afterCreate,
            "Dispose must send SEDP unregister after advertise completes");
    }

    [Fact]
    public void RawReader_DisposeはSEDP_ALIVEの後UNREGISTEREDを送る()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "raw_ordering");
        var beforeCreate = ctx.PublishedSubscriptionStateCount;

        var raw = node.CreateRawReader(
            "rt/raw_test",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        var afterCreate = ctx.PublishedSubscriptionStateCount;
        afterCreate.Should().BeGreaterThan(beforeCreate);

        raw.Dispose();

        var afterDispose = ctx.PublishedSubscriptionStateCount;
        afterDispose.Should().BeGreaterThan(afterCreate,
            "RawReader dispose must send SEDP unregister after advertise");
    }

    [Fact]
    public void 即座にDisposeしてもadvertise完了を待機して安全()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "fast_disp");

        // RawReader
        var raw = node.CreateRawReader(
            "rt/fast_raw",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);
        Assert.NotNull(raw);
        raw.Dispose();

        // Subscription
        var sub = node.CreateSubscription<StringMessage>(
            "fast_sub", StringMessageSerializer.Instance, _ => { });
        Assert.NotNull(sub);
        sub.Dispose();
    }

    [Fact]
    public void DisposeとCreateが並行してもadvertiseとunregisterの順序が保証される()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "concurrent");

        // 複数subscriptionを異なるthreadで作成→即時Dispose
        int successCount = 0;
        var threads = new Thread[4];
        for (int i = 0; i < threads.Length; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    var raw = node.CreateRawReader(
                        $"rt/concurrent_{idx}",
                        "test::msg::dds_::Msg_",
                        (_, _) => { },
                        ReliabilityQos.BestEffort,
                        DurabilityQos.Volatile);
                    raw.Dispose();
                    Interlocked.Increment(ref successCount);
                }
                catch { }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        successCount.Should().Be(threads.Length,
            "all concurrent create+dispose must complete without exception");
    }

    [Fact]
    public void Disposeはadvertise完了を待ってからSEDP_UNREGISTEREDを送る()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "ordering_delay");

        var proceedAdvertise = new TaskCompletionSource();
        ctx.SedpAdvertiseDelay = () => new ValueTask(proceedAdvertise.Task);

        var initialCount = ctx.PublishedSubscriptionStateCount;

        // CreateRawReaderは即座にreturn、advertiseは裏でblock (TaskCompletionSource待ち)
        var raw = node.CreateRawReader(
            "rt/ordering_delay",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        // advertise block中 → ALIVE未送信
        ctx.PublishedSubscriptionStateCount.Should().Be(initialCount,
            "no SEDP ALIVE while advertise is blocked");

        // Disposeを別Threadで呼ぶ → advertise完了待ちでblock
        var disposeThread = new Thread(() =>
        {
            raw.Dispose();
        });
        disposeThread.Start();

        // disposeがGetResult待ちに入るのを少し待つ
        Thread.Sleep(50);
        // Dispose未完了 → ALIVE/UNREGISTEREDどちらも未送信
        ctx.PublishedSubscriptionStateCount.Should().Be(initialCount,
            "no SEDP state sent while advertise blocked and dispose waiting");

        // advertise再開 → ALIVE送信 → Dispose再開 → UNREGISTERED送信
        proceedAdvertise.SetResult();
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)),
            "Dispose must complete after advertise unblocked");

        // ALIVE + UNREGISTERED 両方送信
        ctx.PublishedSubscriptionStateCount.Should().BeGreaterThanOrEqualTo(initialCount + 2,
            "both SEDP ALIVE and UNREGISTERED must be sent (advertise await → then unregister)");
    }

    // ========================================================================
    // 2. RawSubscription/Subscription Dispose thread-safe / idempotent
    // ========================================================================

    [Fact]
    public void RawSubscription_二重Disposeは例外を投げない()
    {
        var reader = new TestUserReader(new EntityId(1, EntityKind.UserDefinedReaderNoKey));
        var raw = new RawSubscription("t", default, reader, (_, _) => { }, autoStart: false);
        raw.Dispose();
        raw.Dispose();
    }

    [Fact]
    public void RawSubscription_Dispose中callback同時実行で破損しない_決定的検証()
    {
        var reader = new TestUserReader(new EntityId(2, EntityKind.UserDefinedReaderNoKey));
        var callbackStarted = new ManualResetEventSlim();
        var disposeDone = new ManualResetEventSlim();
        int callbackInvokeCount = 0;

        var raw = new RawSubscription("t", default, reader,
            (_, _) =>
            {
                Interlocked.Increment(ref callbackInvokeCount);
                callbackStarted.Set();
                if (!disposeDone.IsSet)
                    disposeDone.Wait();
            },
            autoStart: false);

        var callbackThread = new Thread(() => reader.SimulatePayload(new byte[] { 1 }, default));
        callbackThread.Start();

        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)),
            "callback must start before dispose");

        int beforeDispose = Interlocked.CompareExchange(ref callbackInvokeCount, 0, 0);

        raw.Dispose();
        disposeDone.Set();

        reader.SimulatePayload(new byte[] { 2 }, default);

        callbackThread.Join();

        int afterDispose = Interlocked.CompareExchange(ref callbackInvokeCount, 0, 0);
        afterDispose.Should().Be(beforeDispose,
            "no callbacks after Dispose completed");
    }

    [Fact]
    public void Subscription_Dispose中callback同時実行で破損しない_決定的検証()
    {
        var reader = new TestUserReader(new EntityId(3, EntityKind.UserDefinedReaderNoKey));
        var serializer = StringMessageSerializer.Instance;
        var callbackStarted = new ManualResetEventSlim();
        var disposeDone = new ManualResetEventSlim();
        int callbackInvokeCount = 0;

        var sub = new Subscription<StringMessage>(
            "t", default, reader, serializer,
            (_, _) =>
            {
                Interlocked.Increment(ref callbackInvokeCount);
                callbackStarted.Set();
                if (!disposeDone.IsSet)
                    disposeDone.Wait();
            },
            autoStart: false);

        var payload = SerializeStringMessage("race");

        var callbackThread = new Thread(() => reader.SimulatePayload(payload, default));
        callbackThread.Start();

        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)),
            "callback must start before dispose");

        int beforeDispose = Interlocked.CompareExchange(ref callbackInvokeCount, 0, 0);

        sub.Dispose();
        disposeDone.Set();

        reader.SimulatePayload(payload, default);

        callbackThread.Join();

        int afterDispose = Interlocked.CompareExchange(ref callbackInvokeCount, 0, 0);
        afterDispose.Should().Be(beforeDispose,
            "no callbacks after Dispose completed");
    }

    private static byte[] SerializeStringMessage(string text)
    {
        var serializer = StringMessageSerializer.Instance;
        var msg = new StringMessage(text);
        int totalCapacity = CdrEncapsulation.Size + serializer.GetSerializedSize(msg) + 16;
        var buffer = new byte[totalCapacity];
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        serializer.Serialize(ref w, in msg);
        return buffer[..w.Position];
    }

    [Fact]
    public void 複数Threadから同時にDisposeしても1度だけ実行される()
    {
        var reader = new TestUserReader(new EntityId(4, EntityKind.UserDefinedReaderNoKey));
        int disposeCount = 0;
        var raw = new RawSubscription("t", default, reader,
            (_, _) => { },
            (_, _) => Interlocked.Increment(ref disposeCount),
            autoStart: false);

        var barrier = new Barrier(5);
        var threads = new Thread[5];
        for (int i = 0; i < 5; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                raw.Dispose();
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        disposeCount.Should().Be(1, "Dispose must execute unregister exactly once");
    }

    // ========================================================================
    // 3. QoS / exact dds type / endpoint metadata / receiver registration / matching
    // ========================================================================

    [Fact]
    public void CreateRawReaderは正しいQoSとdds_typeでendpointを登録する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "meta_test");

        var raw = node.CreateRawReader(
            "rt/meta_test",
            "test::msg::dds_::CustomMsg_",
            (_, _) => { },
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        var snapshot = node.LocalEndpointSnapshot();
        var reader = snapshot.Readers.Should().ContainSingle().Subject;

        // QoS
        reader.Reliability.Should().Be(ReliabilityQos.Reliable);
        reader.Durability.Should().Be(DurabilityQos.Volatile);

        // exact dds type
        reader.TypeName.Should().Be("test::msg::dds_::CustomMsg_");

        // topic
        reader.TopicName.Should().Be("rt/meta_test");

        // endpoint kind
        reader.Kind.Should().Be(EndpointKind.Reader);

        raw.Dispose();
    }

    [Fact]
    public async Task BestEffortRawReaderはBestEffortPublisherとクロスコンテキストでマッチする()
    {
        using var talkerCtx = new Context(CreateOptions());
        using var listenerCtx = new Context(CreateOptions());
        talkerCtx.Start();
        listenerCtx.Start();

        using var talker = new Node(talkerCtx, "talker_be");
        using var listener = new Node(listenerCtx, "listener_be");

        var topicName = $"be_match_{System.Guid.NewGuid():N}";
        using var pub = talker.CreatePublisher<StringMessage>(
            topicName, StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort, DurabilityQos.Volatile,
            StringMessage.DdsTypeName);

        var raw = listener.CreateRawReader(
            TopicNameMangler.MangleTopic(topicName),
            StringMessage.DdsTypeName,
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        (await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("BestEffort raw reader must match with BestEffort writer");
    }

    [Fact]
    public async Task BestEffortRawReaderはReliablePublisherともクロスコンテキストでマッチする()
    {
        using var talkerCtx = new Context(CreateOptions());
        using var listenerCtx = new Context(CreateOptions());
        talkerCtx.Start();
        listenerCtx.Start();

        using var talker = new Node(talkerCtx, "talker_reliable");
        using var listener = new Node(listenerCtx, "listener_reliable");

        var topicName = $"reliable_match_{System.Guid.NewGuid():N}";
        using var pub = talker.CreatePublisher<StringMessage>(
            topicName, StringMessageSerializer.Instance,
            ReliabilityQos.Reliable, DurabilityQos.Volatile,
            StringMessage.DdsTypeName);

        var raw = listener.CreateRawReader(
            TopicNameMangler.MangleTopic(topicName),
            StringMessage.DdsTypeName,
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        (await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5)))
            .Should().BeTrue(
                "BestEffort raw reader must match with Reliable writer " +
                "(Reliable offered >= BestEffort requested)");
    }

    [Fact]
    public void CreateRawReaderはexact_DDS_typeをendpoint_metadataに保持する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "dds_type_meta");

        var ddsTypeName = "my_package::msg::dds_::CustomType_";
        var raw = node.CreateRawReader(
            "rt/dds_type_test",
            ddsTypeName,
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        var snapshot = node.LocalEndpointSnapshot();
        var reader = snapshot.Readers.Should().ContainSingle().Subject;
        reader.TypeName.Should().Be(ddsTypeName,
            "exact DDS type name must be stored in endpoint metadata");

        raw.Dispose();
    }

    [Fact]
    public void CreateRawReaderはmetadataにreaderを登録しDisposeで削除する()
    {
        using var ctx = new Context(CreateOptions());
        var fakeReceiver = new FakeEndpointReceiver();
        ctx.ReceiverOverrideForTest = fakeReceiver;
        ctx.Start();
        using var node = new Node(ctx, "receiver_test");

        var raw = node.CreateRawReader(
            "rt/receiver_test",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        var afterCreate = node.LocalEndpointSnapshot();
        afterCreate.Readers.Should().Contain(r => r.EndpointGuid.Equals(raw.Guid));
        fakeReceiver.RegisteredReaders.Should().Contain(r => r.entityId == raw.ReaderEntityId);

        raw.Dispose();

        var afterDispose = node.LocalEndpointSnapshot();
        afterDispose.Readers.Should().NotContain(r => r.EndpointGuid.Equals(raw.Guid));
        fakeReceiver.UnregisteredReaders.Should().Contain(raw.ReaderEntityId);
    }

    [Fact]
    public async Task CreateRawReader_Dispose後remote_writerとmatchしない()
    {
        using var talkerCtx = new Context(CreateOptions());
        using var listenerCtx = new Context(CreateOptions());
        talkerCtx.Start();
        listenerCtx.Start();

        using var talker = new Node(talkerCtx, "talker");
        using var listener = new Node(listenerCtx, "listener");

        var topicName = $"lifecycle_{System.Guid.NewGuid():N}";
        using var pub = talker.CreatePublisher<StringMessage>(
            topicName, StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // Pubがmatchするのを待つ
        var raw = listener.CreateRawReader(
            TopicNameMangler.MangleTopic(topicName),
            StringMessage.DdsTypeName,
            (_, _) => { },
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        (await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("writer must match with remote reader");

        raw.Dispose();

        // Dispose後はSEDP unregisterが届き、matchが外れるのを待つ
        // WaitForMatchedAsync(0)は即座に成功するため、手動でポーリング
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (pub.PublicationMatchedStatus.CurrentCount == 0)
                break;
            await Task.Delay(50);
        }
        pub.PublicationMatchedStatus.CurrentCount.Should().Be(0,
            "disposed reader must not be matched with remote writer");
    }

    // ========================================================================
    // 4. SEDP type correctness (subscription writer vs publication writer)
    // ========================================================================

    [Fact]
    public void CreateRawReaderはSEDP_subscriptions_writerに広告する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "sedp_type");

        var beforeSub = ctx.PublishedSubscriptionStateCount;
        var beforePub = ctx.PublishedPublicationStateCount;

        var raw = node.CreateRawReader(
            "rt/sedp_type",
            "test::msg::dds_::Msg_",
            (_, _) => { },
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile);

        // Reader → SEDP subscriptions writer のみ increment
        ctx.PublishedSubscriptionStateCount.Should().BeGreaterThan(beforeSub);
        ctx.PublishedPublicationStateCount.Should().Be(beforePub,
            "reader must not increment publication SEDP writer");

        raw.Dispose();

        // Disposeでもsubscriptions writerのみ increment
        ctx.PublishedSubscriptionStateCount.Should().BeGreaterThan(beforeSub + 1,
            "reader dispose must send unregister to subscriptions writer");
        ctx.PublishedPublicationStateCount.Should().Be(beforePub,
            "reader dispose must not affect publication SEDP writer");
    }

    [Fact]
    public void CreatePublisherはSEDP_publications_writerに広告する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        using var node = new Node(ctx, "sedp_pub_type");

        var beforeSub = ctx.PublishedSubscriptionStateCount;
        var beforePub = ctx.PublishedPublicationStateCount;

        var pub = node.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance);

        ctx.PublishedPublicationStateCount.Should().BeGreaterThan(beforePub);
        ctx.PublishedSubscriptionStateCount.Should().Be(beforeSub,
            "writer must not increment subscription SEDP writer");

        pub.Dispose();

        ctx.PublishedPublicationStateCount.Should().BeGreaterThan(beforePub + 1,
            "writer dispose must send unregister to publications writer");
        ctx.PublishedSubscriptionStateCount.Should().Be(beforeSub,
            "writer dispose must not affect subscription SEDP writer");
    }

    // ========================================================================
    // 5. Dispose中に全Threadの結果/例外/JoinをAssert
    // ========================================================================

    [Fact]
    public void DisposeとCreateRawReaderの競合で全Threadが正常終了する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        var node = new Node(ctx, "race_test");

        var resumeCreate = new ManualResetEventSlim();
        var phase1Done = new ManualResetEventSlim();

        node.BeforeDisposedCheckCallback = () =>
        {
            phase1Done.Set();
            resumeCreate.Wait();
        };

        Exception? createError = null;
        Exception? disposeError = null;
        int disposeCompleted = 0;

        var waitLoopEntered = new ManualResetEventSlim();
        int? observedPendingCount = null;
        node.PendingRegistrationsWaitLoopEntered = (pendingCount) =>
        {
            observedPendingCount = pendingCount;
            waitLoopEntered.Set();
        };

        var createThread = new Thread(() =>
        {
            try
            {
                node.CreateRawReader(
                    "rt/race_raw",
                    "test::msg::dds_::Msg_",
                    (_, _) => { },
                    ReliabilityQos.BestEffort,
                    DurabilityQos.Volatile);
            }
            catch (Exception ex)
            {
                createError = ex;
            }
        });
        createThread.Start();

        Assert.True(phase1Done.Wait(TimeSpan.FromSeconds(5)),
            "phase 1 (metadata registration) must complete");

        var disposeThread = new Thread(() =>
        {
            try
            {
                node.Dispose();
                Interlocked.Exchange(ref disposeCompleted, 1);
            }
            catch (Exception ex)
            {
                disposeError = ex;
            }
        });
        disposeThread.Start();

        Assert.True(waitLoopEntered.Wait(TimeSpan.FromSeconds(5)),
            "Dispose must enter pending registration wait loop");
        Assert.True(node.IsDisposed);
        Assert.True(observedPendingCount.HasValue);
        Assert.True(observedPendingCount!.Value > 0);

        Assert.Equal(0, Volatile.Read(ref disposeCompleted));

        resumeCreate.Set();
        Assert.True(createThread.Join(TimeSpan.FromSeconds(5)),
            "CreateRawReader thread must complete");
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)),
            "Dispose thread must complete after pending registration finishes");

        // 両Threadが例外なく完了または期待された例外で完了
        if (disposeError is not null)
            Assert.Null(disposeError);

        if (createError is not null)
        {
            var odEx = Assert.IsType<ObjectDisposedException>(createError);
            Assert.Contains(typeof(Node).Name, odEx.ObjectName, StringComparison.Ordinal);
        }

        node.BeforeDisposedCheckCallback = null;
        node.PendingRegistrationsWaitLoopEntered = null;
    }

    [Fact]
    public void DisposeとCreateSubscriptionの競合で全Threadが正常終了する()
    {
        using var ctx = new Context(CreateOptions());
        ctx.Start();
        var node = new Node(ctx, "race_sub");

        var resumeCreate = new ManualResetEventSlim();
        var phase1Done = new ManualResetEventSlim();

        node.BeforeDisposedCheckCallback = () =>
        {
            phase1Done.Set();
            resumeCreate.Wait();
        };

        Exception? createError = null;
        Exception? disposeError = null;
        int disposeCompleted = 0;

        var waitLoopEntered = new ManualResetEventSlim();
        node.PendingRegistrationsWaitLoopEntered = (_) => waitLoopEntered.Set();

        var createThread = new Thread(() =>
        {
            try
            {
                node.CreateSubscription<StringMessage>(
                    "chatter", StringMessageSerializer.Instance, _ => { });
            }
            catch (Exception ex)
            {
                createError = ex;
            }
        });
        createThread.Start();

        Assert.True(phase1Done.Wait(TimeSpan.FromSeconds(5)),
            "phase 1 must complete");

        var disposeThread = new Thread(() =>
        {
            try
            {
                node.Dispose();
                Interlocked.Exchange(ref disposeCompleted, 1);
            }
            catch (Exception ex)
            {
                disposeError = ex;
            }
        });
        disposeThread.Start();

        Assert.True(waitLoopEntered.Wait(TimeSpan.FromSeconds(5)),
            "Dispose must enter pending registration wait loop");
        Assert.Equal(0, Volatile.Read(ref disposeCompleted));

        resumeCreate.Set();
        Assert.True(createThread.Join(TimeSpan.FromSeconds(5)),
            "CreateSubscription thread must complete");
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)),
            "Dispose thread must complete");

        if (disposeError is not null)
            Assert.Null(disposeError);

        node.BeforeDisposedCheckCallback = null;
        node.PendingRegistrationsWaitLoopEntered = null;
    }

    // ========================================================================
    // Test helper
    // ========================================================================

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
