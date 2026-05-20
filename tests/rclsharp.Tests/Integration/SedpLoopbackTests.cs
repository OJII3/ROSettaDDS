using System.Net;
using Rclsharp.Common;
using Rclsharp.Dds;
using Rclsharp.Dds.QoS;
using Rclsharp.Discovery;
using Rclsharp.Msgs.Std;
using Rclsharp.Transport;

namespace Rclsharp.Tests.Integration;

public class SedpLoopbackTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
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
        var userUcB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u));

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

        return new TestEnv
        {
            Hub = hub,
            ParticipantA = new DomainParticipant(optionsA),
            ParticipantB = new DomainParticipant(optionsB),
        };
    }

    [Fact]
    public async Task pA_の_Publisher_endpoint_を_pB_が_SEDP_で発見()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var writerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                writerSeenByB.TrySetResult(ep);
            }
        };

        // pA に Publisher を作る (SEDP に登録される)
        using var pub = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        var ep = await writerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        ep.Data.TopicName.Should().Be("rt/chatter");
        ep.Data.TypeName.Should().Be(StringMessage.DdsTypeName);
        ep.Data.Kind.Should().Be(EndpointKind.Writer);
        ep.Data.ParticipantGuid.Prefix.Should().Be(pA.GuidPrefix);
        ep.Data.EndpointGuid.EntityId.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey));
    }

    [Fact]
    public async Task Publisher_endpoint_は_msg型の_DdsTypeName_を既定で広告する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var writerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                writerSeenByB.TrySetResult(ep);
            }
        };

        using var pub = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance);

        pA.Start();
        pB.Start();

        var ep = await writerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        ep.Data.TypeName.Should().Be(StringMessage.DdsTypeName);
    }

    [Fact]
    public async Task Publisher_endpoint_は指定した_reliability_QoS_を広告する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var writerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                writerSeenByB.TrySetResult(ep);
            }
        };

        using var pub = pA.CreatePublisher<StringMessage>(
            "best_effort_chatter",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort);

        pA.Start();
        pB.Start();

        var ep = await writerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        ep.Data.TopicName.Should().Be("rt/best_effort_chatter");
        ep.Data.Reliability.Should().Be(ReliabilityQos.BestEffort);
    }

    [Fact]
    public async Task pA_の_Subscription_endpoint_を_pB_が_SEDP_で発見()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var readerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.ReaderDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                readerSeenByB.TrySetResult(ep);
            }
        };

        using var sub = pA.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        var ep = await readerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        ep.Data.TopicName.Should().Be("rt/chatter");
        ep.Data.Kind.Should().Be(EndpointKind.Reader);
        ep.Data.EndpointGuid.EntityId.Should().Be(new EntityId(0x000005u, EntityKind.UserDefinedReaderNoKey));
    }

    [Fact]
    public async Task Publisher_Dispose_で_SEDP_unregister_が送られ_remote_writer_が消える()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var writerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                writerSeenByB.TrySetResult(ep);
            }
        };
        pB.DiscoveryDb.WriterLost += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                writerLostByB.TrySetResult(ep);
            }
        };

        var pub = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        var seen = await writerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        pub.Dispose();

        var lost = await writerLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost.Data.EndpointGuid.Should().Be(seen.Data.EndpointGuid);
        pB.DiscoveryDb.WriterSnapshot().Should().NotContain(ep => ep.Data.EndpointGuid.Equals(seen.Data.EndpointGuid));
    }

    [Fact]
    public async Task Subscription_Dispose_で_SEDP_unregister_が送られ_remote_reader_が消える()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var readerSeenByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.ReaderDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                readerSeenByB.TrySetResult(ep);
            }
        };
        pB.DiscoveryDb.ReaderLost += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix))
            {
                readerLostByB.TrySetResult(ep);
            }
        };

        var sub = pA.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        var seen = await readerSeenByB.Task.WaitAsync(DiscoveryTimeout);
        sub.Dispose();

        var lost = await readerLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost.Data.EndpointGuid.Should().Be(seen.Data.EndpointGuid);
        pB.DiscoveryDb.ReaderSnapshot().Should().NotContain(ep => ep.Data.EndpointGuid.Equals(seen.Data.EndpointGuid));
    }

    [Fact]
    public async Task 同じ_topic_の複数_Subscription_は別endpointとして_unregister_される()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var readersSeenByB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReaderLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReaderLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seenCount = 0;
        int lostCount = 0;
        pB.DiscoveryDb.ReaderDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)
                && ep.Data.TopicName == "rt/chatter"
                && Interlocked.Increment(ref seenCount) == 2)
            {
                readersSeenByB.TrySetResult();
            }
        };
        pB.DiscoveryDb.ReaderLost += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)
                && ep.Data.TopicName == "rt/chatter")
            {
                if (Interlocked.Increment(ref lostCount) == 1)
                {
                    firstReaderLostByB.TrySetResult(ep);
                }
                else
                {
                    secondReaderLostByB.TrySetResult(ep);
                }
            }
        };

        var sub1 = pA.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        var sub2 = pA.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        await readersSeenByB.Task.WaitAsync(DiscoveryTimeout);
        sub1.Guid.Should().NotBe(sub2.Guid);
        pB.DiscoveryDb.ReaderSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(sub1.Guid));
        pB.DiscoveryDb.ReaderSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(sub2.Guid));

        sub1.Dispose();
        var lost1 = await firstReaderLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost1.Data.EndpointGuid.Should().Be(sub1.Guid);
        pB.DiscoveryDb.ReaderSnapshot().Should().NotContain(ep => ep.Data.EndpointGuid.Equals(sub1.Guid));
        pB.DiscoveryDb.ReaderSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(sub2.Guid));

        sub2.Dispose();
        var lost2 = await secondReaderLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost2.Data.EndpointGuid.Should().Be(sub2.Guid);
    }

    [Fact]
    public async Task 同じ_topic_の複数_Publisher_は別endpointとして_unregister_される()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var writersSeenByB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWriterLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWriterLostByB = new TaskCompletionSource<RemoteEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seenCount = 0;
        int lostCount = 0;
        pB.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)
                && ep.Data.TopicName == "rt/chatter"
                && Interlocked.Increment(ref seenCount) == 2)
            {
                writersSeenByB.TrySetResult();
            }
        };
        pB.DiscoveryDb.WriterLost += ep =>
        {
            if (ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)
                && ep.Data.TopicName == "rt/chatter")
            {
                if (Interlocked.Increment(ref lostCount) == 1)
                {
                    firstWriterLostByB.TrySetResult(ep);
                }
                else
                {
                    secondWriterLostByB.TrySetResult(ep);
                }
            }
        };

        var pub1 = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);
        var pub2 = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        await writersSeenByB.Task.WaitAsync(DiscoveryTimeout);
        pub1.Guid.Should().NotBe(pub2.Guid);
        pB.DiscoveryDb.WriterSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(pub1.Guid));
        pB.DiscoveryDb.WriterSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(pub2.Guid));

        pub1.Dispose();
        var lost1 = await firstWriterLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost1.Data.EndpointGuid.Should().Be(pub1.Guid);
        pB.DiscoveryDb.WriterSnapshot().Should().NotContain(ep => ep.Data.EndpointGuid.Equals(pub1.Guid));
        pB.DiscoveryDb.WriterSnapshot().Should().Contain(ep => ep.Data.EndpointGuid.Equals(pub2.Guid));

        pub2.Dispose();
        var lost2 = await secondWriterLostByB.Task.WaitAsync(DiscoveryTimeout);
        lost2.Data.EndpointGuid.Should().Be(pub2.Guid);
    }

    [Fact]
    public async Task 双方の_Pub_Sub_を_互いに_発見()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var pubA = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);
        using var subB = pB.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance, (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        // SEDP 周期 (50ms) を 4 回ぶん待つ
        await Task.Delay(300);

        // pB は pA の Writer (chatter) を見ているはず
        pB.DiscoveryDb.WriterCount.Should().BeGreaterThan(0);
        var pbWriters = pB.DiscoveryDb.WriterSnapshot();
        pbWriters.Should().Contain(ep => ep.Data.TopicName == "rt/chatter");

        // pA は pB の Reader (chatter) を見ているはず
        pA.DiscoveryDb.ReaderCount.Should().BeGreaterThan(0);
        var paReaders = pA.DiscoveryDb.ReaderSnapshot();
        paReaders.Should().Contain(ep => ep.Data.TopicName == "rt/chatter");
    }

    [Fact]
    public async Task SEDP_と_並行して_Best_Effort_配信が成立する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            typeName: StringMessage.DdsTypeName);

        using var pub = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        await WaitUntilAsync(() =>
            pB.DiscoveryDb.WriterSnapshot().Any(ep => ep.Data.TopicName == "rt/chatter"
                                                   && ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)));
        await WaitUntilAsync(() =>
            pA.DiscoveryDb.ReaderSnapshot().Any(ep => ep.Data.TopicName == "rt/chatter"
                                                   && ep.Data.ParticipantGuid.Prefix.Equals(pB.GuidPrefix)));

        await pub.PublishAsync(new StringMessage("via sedp era"));

        var msg = await receivedTcs.Task.WaitAsync(DiscoveryTimeout);
        msg.Data.Should().Be("via sedp era");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + DiscoveryTimeout;
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
