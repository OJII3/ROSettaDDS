using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Discovery;

public class SpdpLoopbackTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task 二つの_DomainParticipant_が_loopback_multicast_で_相互発見する()
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var multicastLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var unicastA = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var unicastB = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);

        // 同一 multicast Locator に listener を 2 つ登録 (multicast 疑似)
        var mcA = hub.Create(multicastLoc);
        var mcB = hub.Create(multicastLoc);
        var ucA = hub.Create(unicastA);
        var ucB = hub.Create(unicastB);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);
        var userMcA = hub.Create(userMcLoc);
        var userMcB = hub.Create(userMcLoc);
        var userUcA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));
        var userUcB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u));

        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0,
            ParticipantId = 1,
            EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = mcA,
            CustomUnicastTransport = ucA,
            CustomUserMulticastTransport = userMcA,
            CustomUserUnicastTransport = userUcA,
        };
        var optionsB = new DomainParticipantOptions
        {
            DomainId = 0,
            ParticipantId = 2,
            EntityName = "node_b",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = mcB,
            CustomUnicastTransport = ucB,
            CustomUserMulticastTransport = userMcB,
            CustomUserUnicastTransport = userUcB,
        };

        using var pA = new DomainParticipant(optionsA);
        using var pB = new DomainParticipant(optionsB);

        var bSeenByA = new TaskCompletionSource<RemoteParticipant>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aSeenByB = new TaskCompletionSource<RemoteParticipant>(TaskCreationOptions.RunContinuationsAsynchronously);

        pA.DiscoveryDb.ParticipantDiscovered += rp =>
        {
            if (rp.GuidPrefix.Equals(pB.GuidPrefix)) bSeenByA.TrySetResult(rp);
        };
        pB.DiscoveryDb.ParticipantDiscovered += rp =>
        {
            if (rp.GuidPrefix.Equals(pA.GuidPrefix)) aSeenByB.TrySetResult(rp);
        };

        pA.Start();
        pB.Start();

        var remoteB = await bSeenByA.Task.WaitAsync(DiscoveryTimeout);
        var remoteA = await aSeenByB.Task.WaitAsync(DiscoveryTimeout);

        remoteB.Data.EntityName.Should().Be("node_b");
        remoteB.Data.Guid.EntityId.Should().Be(BuiltinEntityIds.Participant);
        remoteB.Data.MetatrafficMulticastLocators.Should().Contain(multicastLoc);
        remoteB.Data.MetatrafficUnicastLocators.Should().Contain(unicastB);

        remoteA.Data.EntityName.Should().Be("node_a");
        remoteA.Data.MetatrafficUnicastLocators.Should().Contain(unicastA);

        // DB に互いの 1 件だけが入っている (自分自身は ignored)
        pA.DiscoveryDb.Count.Should().Be(1);
        pB.DiscoveryDb.Count.Should().Be(1);
    }

    [Fact]
    public async Task SPDP_DATA_を_unicast_transport_経由で受信しても_DiscoveryDb_に反映される()
    {
        // Arrange: pA は multicast のみ、pB は unicast のみで SPDP を受信する構成
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var multicastLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var unicastA = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var unicastB = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        // pA の multicast transport: multicastLoc に参加
        // pB の multicast transport: multicastLoc には参加させない → unicast のみで受信させる
        var mcA = hub.Create(multicastLoc);
        var mcBIsolated = hub.Create(Locator.FromUdpV4(multicastIp, 19999u)); // 別ポート → multicast から孤立
        var ucA = hub.Create(unicastA);
        var ucB = hub.Create(unicastB);
        var userMcA = hub.Create(userMcLoc);
        var userMcB = hub.Create(userMcLoc);
        var userUcA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));
        var userUcB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u));

        // pA の SpdpWriter が送る multicastLoc を pB の unicast に転送するスニファ
        // → pA が multicast へ送った SPDP DATA を pB の unicast に手動ルーティング
        var sniffer = hub.Create(Locator.FromUdpV4(multicastIp, 18888u));
        sniffer.JoinGroup(multicastLoc);
        sniffer.Received += (packet, _) =>
        {
            // multicast で届いた SPDP パケットを pB の unicast transport へ注入
            ucB.RaiseReceived(packet, unicastA);
        };

        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0,
            ParticipantId = 1,
            EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = mcA,
            CustomUnicastTransport = ucA,
            CustomUserMulticastTransport = userMcA,
            CustomUserUnicastTransport = userUcA,
        };
        var optionsB = new DomainParticipantOptions
        {
            DomainId = 0,
            ParticipantId = 2,
            EntityName = "node_b",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = mcBIsolated,
            CustomUnicastTransport = ucB,
            CustomUserMulticastTransport = userMcB,
            CustomUserUnicastTransport = userUcB,
        };

        using var pA = new DomainParticipant(optionsA);
        using var pB = new DomainParticipant(optionsB);

        var aSeenByB = new TaskCompletionSource<RemoteParticipant>(TaskCreationOptions.RunContinuationsAsynchronously);
        pB.DiscoveryDb.ParticipantDiscovered += rp =>
        {
            if (rp.GuidPrefix.Equals(pA.GuidPrefix)) aSeenByB.TrySetResult(rp);
        };

        pA.Start();
        pB.Start();

        // pB は multicast を受信できないが unicast 経由のルーティングで pA を発見できるはず
        var remoteA = await aSeenByB.Task.WaitAsync(DiscoveryTimeout);

        remoteA.Data.EntityName.Should().Be("node_a");
        remoteA.Data.MetatrafficUnicastLocators.Should().Contain(unicastA);
        pB.DiscoveryDb.Count.Should().Be(1);
    }

    [Fact]
    public async Task SpdpWriter_は_自分の_GuidPrefix_を持つ_message_を送る()
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var multicastLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var unicastA = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var sniff = Locator.FromUdpV4(IPAddress.Parse("10.0.0.99"), 9999u);

        var mcA = hub.Create(multicastLoc);
        var ucA = hub.Create(unicastA);
        var sniffer = hub.Create(sniff);
        sniffer.JoinGroup(multicastLoc);

        var capturedSource = new TaskCompletionSource<Locator>(TaskCreationOptions.RunContinuationsAsynchronously);
        sniffer.Received += (_, src) => capturedSource.TrySetResult(src);

        var options = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = mcA,
            CustomUnicastTransport = ucA,
        };
        using var pA = new DomainParticipant(options);
        pA.Start();

        var src = await capturedSource.Task.WaitAsync(DiscoveryTimeout);
        // SpdpWriter は mcA から送信するので source は mcA.LocalLocator
        src.Should().Be(multicastLoc);
    }
}
