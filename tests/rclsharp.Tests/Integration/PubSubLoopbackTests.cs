using System.Net;
using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Dds;
using Rclsharp.Discovery;
using Rclsharp.Msgs.Std;
using Rclsharp.Rtps;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Transport;

namespace Rclsharp.Tests.Integration;

public class PubSubLoopbackTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
        public required Locator UserUnicastBLocator { get; init; }
    }

    private readonly struct AnonymousMessage
    {
        public AnonymousMessage(string data)
        {
            Data = data;
        }

        public string Data { get; }
    }

    private sealed class AnonymousMessageSerializer : ICdrSerializer<AnonymousMessage>
    {
        public static readonly AnonymousMessageSerializer Instance = new();

        public bool IsKeyed => false;

        public int GetSerializedSize(in AnonymousMessage value)
            => 4 + (value.Data is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value.Data)) + 1;

        public void Serialize(ref CdrWriter writer, in AnonymousMessage value)
            => writer.WriteString(value.Data);

        public void Deserialize(ref CdrReader reader, out AnonymousMessage value)
            => value = new AnonymousMessage(reader.ReadString());

        public void SerializeKey(ref CdrWriter writer, in AnonymousMessage value)
        {
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PendingCount
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
            }
        }

        public void Drain()
        {
            while (true)
            {
                SendOrPostCallback callback;
                object? state;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    (callback, state) = _queue.Dequeue();
                }

                callback(state);
            }
        }
    }

    private static TestEnv CreatePair()
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        var spdpA = hub.Create(spdpLoc);
        var spdpB = hub.Create(spdpLoc);
        var ucA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u));
        var ucB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u));
        var userMcA = hub.Create(userMcLoc);
        var userMcB = hub.Create(userMcLoc);
        var userUcA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));
        var userUcBLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u);
        var userUcB = hub.Create(userUcBLoc);

        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = spdpA,
            CustomUnicastTransport = ucA,
            CustomUserMulticastTransport = userMcA,
            CustomUserUnicastTransport = userUcA,
        };
        var optionsB = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 2, EntityName = "node_b",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = spdpB,
            CustomUnicastTransport = ucB,
            CustomUserMulticastTransport = userMcB,
            CustomUserUnicastTransport = userUcB,
        };

        var pA = new DomainParticipant(optionsA);
        var pB = new DomainParticipant(optionsB);
        return new TestEnv { Hub = hub, ParticipantA = pA, ParticipantB = pB, UserUnicastBLocator = userUcBLoc };
    }

    [Fact]
    public async Task chatter_を_Publisher_から_Subscription_に届ける()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg));

        using var pub = pA.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await WaitUntilDiscoveredAsync(pA, pB, "rt/chatter");

        await pub.PublishAsync(new StringMessage("hello rclsharp"));

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("hello rclsharp");
    }

    [Fact]
    public async Task Subscription_handler_を指定した_SynchronizationContext_へ配送する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;
        var context = new QueuedSynchronizationContext();
        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = pB.CreateSubscription<StringMessage>(
            "context_chatter",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            handlerContext: context);

        using var pub = pA.CreatePublisher<StringMessage>("context_chatter", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await WaitUntilDiscoveredAsync(pA, pB, "rt/context_chatter");

        await pub.PublishAsync(new StringMessage("main-thread"));

        await WaitUntilAsync(() => context.PendingCount > 0);
        receivedTcs.Task.IsCompleted.Should().BeFalse("handler は context を drain するまで実行されない");

        context.Drain();
        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("main-thread");
    }

    [Fact]
    public async Task 異なる_topic_は_互いに届かない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        bool received = false;
        using var sub = pB.CreateSubscription<StringMessage>(
            "topic_a",
            StringMessageSerializer.Instance,
            (_, _) => received = true);

        using var pub = pA.CreatePublisher<StringMessage>("topic_b", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await pub.PublishAsync(new StringMessage("should not arrive"));
        await Task.Delay(200); // 受信猶予

        received.Should().BeFalse();
    }

    [Fact]
    public async Task 複数件_publish_すると_順番に_受信()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<string>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                lock (lockObj) { received.Add(msg.Data); }
            });

        using var pub = pA.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await WaitUntilDiscoveredAsync(pA, pB, "rt/chatter");

        for (int i = 0; i < 10; i++)
        {
            await pub.PublishAsync(new StringMessage($"msg{i}"));
        }

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj)
            {
                if (received.Count == 10)
                {
                    break;
                }
            }
            await Task.Delay(10);
        }

        lock (lockObj)
        {
            received.Should().HaveCount(10);
            received.Should().Equal(Enumerable.Range(0, 10).Select(i => $"msg{i}"));
        }
    }

    [Fact]
    public async Task 同一_participant_内で_自分の_publish_を_subscription_が_受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // pA で pub と sub の両方
        using var sub = pA.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg));
        using var pub = pA.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await pub.PublishAsync(new StringMessage("self-pub"));
        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("self-pub");
    }

    [Fact]
    public async Task type_nameが空のendpoint同士は同一topicでもmatchしない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        bool received = false;
        using var sub = pA.CreateSubscription<AnonymousMessage>(
            "anonymous",
            AnonymousMessageSerializer.Instance,
            (_, _) => received = true);
        using var pub = pA.CreatePublisher<AnonymousMessage>(
            "anonymous",
            AnonymousMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await pub.PublishAsync(new AnonymousMessage("must not arrive"));
        await Task.Delay(200);

        received.Should().BeFalse("空 type name は wildcard ではなく明示 type 不在として扱うべき");
    }

    [Fact]
    public async Task late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var pub = pA.CreatePublisher<StringMessage>("volatile_topic", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        await pub.PublishAsync(new StringMessage("before subscription"));
        await Task.Delay(100);

        bool received = false;
        using var sub = pB.CreateSubscription<StringMessage>(
            "volatile_topic",
            StringMessageSerializer.Instance,
            (_, _) => received = true);

        await Task.Delay(300);

        received.Should().BeFalse();
    }

    [Fact]
    public async Task SEDP_で発見した_remote_writer_の_unicast_DATA_を受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            StringMessage.DdsTypeName);

        pB.Start();

        var remotePrefix = GuidPrefix.CreateForCurrentProcess(VendorId.EProsimaFastDds);
        var remoteWriterId = new EntityId(0x123456u, EntityKind.UserDefinedWriterNoKey);
        var remoteWriterGuid = new Rclsharp.Common.Guid(remotePrefix, remoteWriterId);
        pB.DiscoveryDb.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = remoteWriterGuid,
            ParticipantGuid = new Rclsharp.Common.Guid(remotePrefix, EntityId.Participant),
            TopicName = "rt/chatter",
            TypeName = StringMessage.DdsTypeName,
        }, DateTime.UtcNow);

        using var remoteTransport = env.Hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.10"), 9000u));
        var payload = SerializeStringPayload(new StringMessage("fastdds-style unicast"));
        var packet = BuildDataPacket(remotePrefix, remoteWriterId, EntityId.Unknown, payload);
        await remoteTransport.SendAsync(packet, env.UserUnicastBLocator);

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("fastdds-style unicast");
    }

    [Fact]
    public async Task SEDP_で発見した_remote_writer_の_multicast_DATA_FRAG_を再構成して受信できる()
    {
        var env = CreatePair();
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "image_text",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            StringMessage.DdsTypeName);

        pB.Start();

        var remotePrefix = GuidPrefix.CreateForCurrentProcess(VendorId.EProsimaFastDds);
        var remoteWriterId = new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey);
        var remoteWriterGuid = new Rclsharp.Common.Guid(remotePrefix, remoteWriterId);
        pB.DiscoveryDb.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = remoteWriterGuid,
            ParticipantGuid = new Rclsharp.Common.Guid(remotePrefix, EntityId.Participant),
            TopicName = "rt/image_text",
            TypeName = StringMessage.DdsTypeName,
        }, DateTime.UtcNow);

        using var remoteTransport = env.Hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.10"), 9001u));
        var payload = SerializeStringPayload(new StringMessage("fragmented fastdds-style payload"));
        const ushort fragmentSize = 8;
        int fragmentCount = (payload.Length + fragmentSize - 1) / fragmentSize;

        for (int fragmentIndex = fragmentCount - 1; fragmentIndex >= 0; fragmentIndex--)
        {
            int offset = fragmentIndex * fragmentSize;
            int length = Math.Min(fragmentSize, payload.Length - offset);
            var fragmentPayload = payload.AsSpan(offset, length).ToArray();
            var packet = BuildDataFragPacket(
                remotePrefix,
                remoteWriterId,
                sub.Guid.EntityId,
                new SequenceNumber(11),
                fragmentStartingNumber: (uint)fragmentIndex + 1,
                fragmentsInSubmessage: 1,
                fragmentSize: fragmentSize,
                sampleSize: (uint)payload.Length,
                fragmentPayload);
            await remoteTransport.SendAsync(packet, pB.UserMulticastDestination);
        }

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("fragmented fastdds-style payload");
    }

    [Fact]
    public async Task SEDP_で発見した_remote_writer_の_unicast_DATA_FRAG_を再構成して受信できる()
    {
        var env = CreatePair();
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "image_text_unicast",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            StringMessage.DdsTypeName);

        pB.Start();

        var remotePrefix = GuidPrefix.CreateForCurrentProcess(VendorId.EProsimaFastDds);
        var remoteWriterId = new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey);
        var remoteWriterGuid = new Rclsharp.Common.Guid(remotePrefix, remoteWriterId);
        pB.DiscoveryDb.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = remoteWriterGuid,
            ParticipantGuid = new Rclsharp.Common.Guid(remotePrefix, EntityId.Participant),
            TopicName = "rt/image_text_unicast",
            TypeName = StringMessage.DdsTypeName,
        }, DateTime.UtcNow);

        using var remoteTransport = env.Hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.10"), 9002u));
        var payload = SerializeStringPayload(new StringMessage("fragmented unicast payload"));
        const ushort fragmentSize = 7;
        int fragmentCount = (payload.Length + fragmentSize - 1) / fragmentSize;

        for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
        {
            int offset = fragmentIndex * fragmentSize;
            int length = Math.Min(fragmentSize, payload.Length - offset);
            var packet = BuildDataFragPacket(
                remotePrefix,
                remoteWriterId,
                sub.Guid.EntityId,
                new SequenceNumber(12),
                fragmentStartingNumber: (uint)fragmentIndex + 1,
                fragmentsInSubmessage: 1,
                fragmentSize: fragmentSize,
                sampleSize: (uint)payload.Length,
                payload.AsMemory(offset, length));
            await remoteTransport.SendAsync(packet, env.UserUnicastBLocator);
        }

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Should().Be("fragmented unicast payload");
    }

    private static byte[] SerializeStringPayload(StringMessage value)
    {
        var buffer = new byte[128];
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var writer = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        StringMessageSerializer.Instance.Serialize(ref writer, in value);
        return buffer[..writer.Position];
    }

    private static byte[] BuildDataPacket(
        GuidPrefix sourcePrefix,
        EntityId writerId,
        EntityId readerId,
        ReadOnlyMemory<byte> payload)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, sourcePrefix);
        writer.WriteInfoTimestamp(new InfoTimestampSubmessage(Rclsharp.Common.Time.Now()));
        writer.WriteData(new DataSubmessage(
            readerEntityId: readerId,
            writerEntityId: writerId,
            writerSn: new SequenceNumber(1),
            serializedPayload: payload,
            dataPresent: true));
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildDataFragPacket(
        GuidPrefix sourcePrefix,
        EntityId writerId,
        EntityId readerId,
        SequenceNumber sequenceNumber,
        uint fragmentStartingNumber,
        ushort fragmentsInSubmessage,
        ushort fragmentSize,
        uint sampleSize,
        ReadOnlyMemory<byte> fragmentPayload)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.EProsimaFastDds, sourcePrefix);
        writer.WriteDataFrag(new DataFragSubmessage(
            readerEntityId: readerId,
            writerEntityId: writerId,
            writerSn: sequenceNumber,
            fragmentStartingNumber: fragmentStartingNumber,
            fragmentsInSubmessage: fragmentsInSubmessage,
            fragmentSize: fragmentSize,
            sampleSize: sampleSize,
            serializedPayloadFragment: fragmentPayload));
        return writer.WrittenSpan.ToArray();
    }

    private static async Task WaitUntilDiscoveredAsync(DomainParticipant writerParticipant, DomainParticipant readerParticipant, string ddsTopic)
    {
        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            bool writerSeen = readerParticipant.DiscoveryDb.WriterSnapshot()
                .Any(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(writerParticipant.GuidPrefix));
            bool readerSeen = writerParticipant.DiscoveryDb.ReaderSnapshot()
                .Any(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(readerParticipant.GuidPrefix));
            if (writerSeen && readerSeen)
            {
                return;
            }
            await Task.Delay(50);
        }

        readerParticipant.DiscoveryDb.WriterSnapshot().Should()
            .Contain(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(writerParticipant.GuidPrefix));
        writerParticipant.DiscoveryDb.ReaderSnapshot().Should()
            .Contain(ep => ep.Data.TopicName == ddsTopic
                        && ep.Data.ParticipantGuid.Prefix.Equals(readerParticipant.GuidPrefix));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        condition().Should().BeTrue();
    }
}
