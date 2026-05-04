using System.Net;
using Rclsharp.Common;
using Rclsharp.Dds;
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

        await pub.PublishAsync(new StringMessage("via sedp era"));

        var msg = await receivedTcs.Task.WaitAsync(DiscoveryTimeout);
        msg.Data.Should().Be("via sedp era");
    }
}
