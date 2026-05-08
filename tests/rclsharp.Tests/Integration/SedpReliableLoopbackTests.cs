using System.Net;
using Rclsharp.Common;
using Rclsharp.Dds;
using Rclsharp.Discovery;
using Rclsharp.Msgs.Std;
using Rclsharp.Rtps.Writer;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Tests.Integration;

public class SedpReliableLoopbackTests
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
        var ucALoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var ucBLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);

        var spdpA = hub.Create(spdpLoc);
        var spdpB = hub.Create(spdpLoc);
        var ucA = hub.Create(ucALoc);
        var ucB = hub.Create(ucBLoc);
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
    public async Task SPDP_発見後に_SEDP_endpoint_が_auto_match_される()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        pA.Start();
        pB.Start();

        // SPDP 周期 (50ms) を 4 サイクル待つ
        await Task.Delay(300);

        // pB の SedpPublicationsWriter は pA の SedpPublicationsReader を match しているはず
        // (matched count > 0 を間接的に確認するため、pA の Pub を作って pB が見えるか試す)
        using var pubA = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        // SEDP Reliable 経由で endpoint が pB に届くまで待つ
        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        bool found = false;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = pB.DiscoveryDb.WriterSnapshot();
            if (snapshot.Any(ep => ep.Data.TopicName == "rt/chatter"
                                && ep.Data.ParticipantGuid.Prefix.Equals(pA.GuidPrefix)))
            {
                found = true;
                break;
            }
            await Task.Delay(50);
        }
        found.Should().BeTrue("pA の Publisher endpoint が SEDP 経由で pB に届くべき");
    }

    [Fact]
    public async Task SEDP_writer_の_HighestAcked_が_reliable_handshake_で進む()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        pA.Start();
        pB.Start();

        using var pubA = pA.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        // SPDP→SEDP auto-match→DATA→HB→ACKNACK の周回を待つ
        var pubsWriter = GetSedpPublicationsWriter(pA);
        pubsWriter.Should().NotBeNull();

        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        bool acked = false;
        while (DateTime.UtcNow < deadline)
        {
            // pB の SEDP reader (Pub) が pA の SEDP writer (Pub) に対して送り返した ACKNACK で
            // pubsWriter のいずれかの ReaderProxy.HighestAcked が >= 1 になるはず
            foreach (var proxy in pubsWriter!.Stateful.MatchedReaders)
            {
                if (proxy.HighestAcked.Value >= 1L)
                {
                    acked = true;
                    break;
                }
            }
            if (acked) break;
            await Task.Delay(50);
        }
        acked.Should().BeTrue("SEDP writer が ACKNACK を受けて HighestAcked が進むべき");
    }

    [Fact]
    public async Task 後発の_Pub_作成も_TRANSIENT_LOCAL_風に_既存ノードへ届く()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        pA.Start();
        pB.Start();
        await Task.Delay(200); // SPDP 安定化

        // pB が起動した後で pA が Pub を作る
        using var pubA = pA.CreatePublisher<StringMessage>(
            "late_topic", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        bool found = false;
        while (DateTime.UtcNow < deadline)
        {
            if (pB.DiscoveryDb.WriterSnapshot().Any(ep => ep.Data.TopicName == "rt/late_topic"))
            {
                found = true;
                break;
            }
            await Task.Delay(50);
        }
        found.Should().BeTrue();
    }

    [Fact]
    public async Task ACK_後も_SEDP_endpoint_history_を保持して_late_participant_へ再送する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);
        using var pC = new DomainParticipant(new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 3, EntityName = "node_c",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = env.Hub.Create(spdpLoc),
            CustomUnicastTransport = env.Hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.3"), 7415u)),
            CustomUserMulticastTransport = env.Hub.Create(userMcLoc),
            CustomUserUnicastTransport = env.Hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.3"), 7416u)),
        });

        pA.Start();
        pB.Start();

        using var pubA = pA.CreatePublisher<StringMessage>(
            "retained_topic", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        await WaitUntilAsync(() =>
            pB.DiscoveryDb.WriterSnapshot().Any(ep => ep.Data.TopicName == "rt/retained_topic"));

        var pubsWriter = GetSedpPublicationsWriter(pA);
        await WaitUntilAsync(() =>
            pubsWriter!.Stateful.MatchedReaders.Any(proxy => proxy.HighestAcked.Value >= 1L));

        pC.Start();

        await WaitUntilAsync(() =>
            pC.DiscoveryDb.WriterSnapshot().Any(ep => ep.Data.TopicName == "rt/retained_topic"),
            because: "ACK 済みでも SEDP endpoint sample は late participant 向けに残るべき");
    }

    [Fact]
    public async Task 既存_remote_reader_発見後に作った_local_publisher_を即時_matchする()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var subB = pB.CreateSubscription<StringMessage>(
            "reader_first_topic",
            StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        await WaitUntilAsync(() =>
            pA.DiscoveryDb.ReaderSnapshot().Any(ep => ep.Data.TopicName == "rt/reader_first_topic"));

        using var pubA = pA.CreatePublisher<StringMessage>(
            "reader_first_topic", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);

        var userWriter = GetUserWriter(pubA);
        await WaitUntilAsync(() =>
            userWriter!.MatchedReaders.Any(proxy => proxy.ReaderGuid.Prefix.Equals(pB.GuidPrefix)),
            because: "local publisher 作成時に既存 remote reader と match するべき");
    }

    private static SedpEndpointWriter? GetSedpPublicationsWriter(DomainParticipant p)
    {
        // private field にアクセスするため reflection
        var field = typeof(DomainParticipant).GetField("_sedpPublicationsWriter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(p) as SedpEndpointWriter;
    }

    private static StatefulWriter? GetUserWriter<T>(Publisher<T> publisher)
    {
        var field = typeof(Publisher<T>).GetField("_writer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(publisher) as StatefulWriter;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because = "")
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

        condition().Should().BeTrue(because);
    }
}
