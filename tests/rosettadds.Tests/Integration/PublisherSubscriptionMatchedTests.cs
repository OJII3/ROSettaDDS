using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherSubscriptionMatchedTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(3);

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
    public async Task Publisher_PublicationMatchedStatus_CurrentCount_が_マッチ後に_1_になる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await pub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "Publisher 側で reader のマッチがタイムアウト");

        var status = pub.PublicationMatchedStatus;
        Assert.Equal(1, status.CurrentCount);
        Assert.Equal(1, status.TotalCount);
        Assert.NotNull(status.LastSubscriptionHandle);
        Assert.Equal(sub.Guid, status.LastSubscriptionHandle!.Value);
    }

    [Fact]
    public async Task Subscription_SubscriptionMatchedStatus_CurrentCount_が_マッチ後に_1_になる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await sub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "Subscription 側で writer のマッチがタイムアウト");

        var status = sub.SubscriptionMatchedStatus;
        Assert.Equal(1, status.CurrentCount);
        Assert.Equal(1, status.TotalCount);
        Assert.NotNull(status.LastPublicationHandle);
        Assert.Equal(pub.Guid, status.LastPublicationHandle!.Value);
    }

    [Fact]
    public async Task TotalCountChange_と_CurrentCountChange_は_read_でリセットされる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "reset_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "reset_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, DiscoveryTimeout);

        var first = pub.PublicationMatchedStatus;
        Assert.Equal(1, first.CurrentCountChange);
        Assert.Equal(1, first.TotalCountChange);

        // 変化なしで再 read すると change は 0
        var second = pub.PublicationMatchedStatus;
        Assert.Equal(0, second.CurrentCountChange);
        Assert.Equal(0, second.TotalCountChange);
        Assert.Equal(first.CurrentCount, second.CurrentCount);
        Assert.Equal(first.TotalCount, second.TotalCount);
    }

    [Fact]
    public async Task BestEffort_subscription_でも_MatchedWriterCount_が_機能する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "be_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName,
            reliability: ReliabilityQos.BestEffort);
        using var pub = pA.CreatePublisher<StringMessage>(
            "be_topic", StringMessageSerializer.Instance,
            reliability: ReliabilityQos.BestEffort,
            durability: DurabilityQos.Volatile,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await sub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "BestEffort subscription 側でマッチがタイムアウト");
        Assert.Equal(1, sub.SubscriptionMatchedStatus.CurrentCount);
    }

    [Fact]
    public async Task 既に達成済みなら_WaitForMatchedAsync_は即_true()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "fast_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "fast_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, DiscoveryTimeout);

        // 既にマッチ済みなので即 true
        bool matched = await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(1));
        Assert.True(matched);
    }
}
