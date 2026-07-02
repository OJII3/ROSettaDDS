// Legacy DomainParticipant API を使った互換性検証。
// ROSettaDDS.Rcl.Context/Node が正本だが、後方互換のため
// `ROSettaDDS.Dds.DomainParticipant` を直接利用する経路の挙動を
// ここで継続的にカバーする。
#pragma warning disable CS0618 // Type or member is obsolete (DomainParticipant)
using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Integration;

public class SedpReliableLoopbackTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(3);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
    }

    private static TestEnv CreatePair(Duration? leaseDuration = null)
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
        var lease = leaseDuration ?? Duration.FromSeconds(20);

        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            LeaseDuration = lease,
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
            LeaseDuration = lease,
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
    public async Task SEDP_writer_history_は_endpoint_GUIDごとに最新_aliveまたは_tombstoneだけを保持する()
    {
        using var f = new SedpEndpointWireFixture();
        var endpoint = CreateWriterEndpoint(
            f.WriterPrefix,
            new EntityId(0x000010u, EntityKind.UserDefinedWriterNoKey),
            "rt/compact_topic",
            "old_type");
        var updatedEndpoint = CreateWriterEndpoint(
            f.WriterPrefix,
            endpoint.EndpointGuid.EntityId,
            endpoint.TopicName,
            "new_type");

        await f.Writer.AddEndpointAsync(endpoint);
        await f.Writer.AddEndpointAsync(updatedEndpoint);

        f.Writer.Stateful.History.Count.Should().Be(1);
        var aliveChange = SingleHistoryChange(f.Writer);
        aliveChange.Kind.Should().Be(ChangeKind.Alive);
        ReadEndpointPayload(aliveChange).TypeName.Should().Be("new_type");

        await f.Writer.UnregisterEndpointAsync(updatedEndpoint);

        f.Writer.Stateful.History.Count.Should().Be(1);
        var tombstoneChange = SingleHistoryChange(f.Writer);
        tombstoneChange.Kind.Should().Be(ChangeKind.NotAliveUnregistered);
        ReadEndpointPayload(tombstoneChange).EndpointGuid.Should().Be(endpoint.EndpointGuid);
    }

    [Fact]
    public async Task late_SEDP_reader_は同一_endpoint_GUIDの最新_aliveだけを受け取る()
    {
        using var f = new SedpEndpointWireFixture();
        var endpoint = CreateWriterEndpoint(
            f.WriterPrefix,
            new EntityId(0x000011u, EntityKind.UserDefinedWriterNoKey),
            "rt/latest_alive",
            "old_type");
        var updatedEndpoint = CreateWriterEndpoint(
            f.WriterPrefix,
            endpoint.EndpointGuid.EntityId,
            endpoint.TopicName,
            "new_type");
        var received = new List<DiscoveredEndpointData>();
        var receivedLock = new object();
        f.Reader.EndpointDataReceived += ep =>
        {
            if (!ep.EndpointGuid.Equals(endpoint.EndpointGuid))
            {
                return;
            }
            lock (receivedLock) { received.Add(ep); }
        };

        await f.Writer.AddEndpointAsync(endpoint);
        await f.Writer.AddEndpointAsync(updatedEndpoint);

        f.Match();

        await WaitUntilAsync(() =>
        {
            lock (receivedLock) { return received.Count > 0; }
        });
        lock (receivedLock)
        {
            received.Should().ContainSingle();
            received[0].TypeName.Should().Be("new_type");
        }
        f.DiscoveryDb.WriterSnapshot()
            .Should()
            .ContainSingle(ep => ep.Data.EndpointGuid.Equals(endpoint.EndpointGuid))
            .Which.Data.TypeName.Should().Be("new_type");
    }

    [Fact]
    public async Task late_SEDP_reader_は_unregister済み_endpointの古い_aliveを受け取らない()
    {
        using var f = new SedpEndpointWireFixture();
        var endpoint = CreateWriterEndpoint(
            f.WriterPrefix,
            new EntityId(0x000012u, EntityKind.UserDefinedWriterNoKey),
            "rt/latest_tombstone",
            StringMessage.DdsTypeName);
        int endpointPayloads = 0;
        int discoveredAlive = 0;
        f.Reader.EndpointDataReceived += ep =>
        {
            if (ep.EndpointGuid.Equals(endpoint.EndpointGuid))
            {
                Interlocked.Increment(ref endpointPayloads);
            }
        };
        f.DiscoveryDb.WriterDiscovered += ep =>
        {
            if (ep.Data.EndpointGuid.Equals(endpoint.EndpointGuid))
            {
                Interlocked.Increment(ref discoveredAlive);
            }
        };

        await f.Writer.AddEndpointAsync(endpoint);
        await f.Writer.UnregisterEndpointAsync(endpoint);

        f.Match();

        await WaitUntilAsync(() => Volatile.Read(ref endpointPayloads) > 0);
        await Task.Delay(100);

        Volatile.Read(ref endpointPayloads).Should().Be(1, "late reader には最新 tombstone だけが再送されるべき");
        Volatile.Read(ref discoveredAlive).Should().Be(0, "古い alive が再送されると remote writer として一度発見されてしまう");
        f.DiscoveryDb.WriterSnapshot()
            .Should()
            .NotContain(ep => ep.Data.EndpointGuid.Equals(endpoint.EndpointGuid));
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

    [Fact]
    public void 既存_remote_reader_endpoint_updateでlocal_writerのlocatorを更新する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;
        using var pubA = pA.CreatePublisher<StringMessage>(
            "locator_update", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);
        var userWriter = GetUserWriter(pubA)!;
        var remotePrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x20, 0x30, 0x01);
        var remoteParticipantGuid = new Guid(remotePrefix, EntityId.Participant);
        var remoteReaderGuid = new Guid(remotePrefix, new EntityId(0x40u, EntityKind.UserDefinedReaderNoKey));
        var firstLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.20"), 8000u);
        var updatedLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.21"), 8001u);
        SeedRemoteParticipant(pA.DiscoveryDb, remotePrefix);

        var firstEndpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = remoteParticipantGuid,
            TopicName = "rt/locator_update",
            TypeName = StringMessage.DdsTypeName,
        };
        firstEndpoint.UnicastLocators.Add(firstLocator);
        pA.DiscoveryDb.UpsertEndpoint(firstEndpoint, DateTime.UtcNow);

        var updatedEndpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = remoteParticipantGuid,
            TopicName = "rt/locator_update",
            TypeName = StringMessage.DdsTypeName,
        };
        updatedEndpoint.UnicastLocators.Add(updatedLocator);
        pA.DiscoveryDb.UpsertEndpoint(updatedEndpoint, DateTime.UtcNow);

        userWriter.GetReaderProxy(remoteReaderGuid)!.UnicastLocator.Should().Be(updatedLocator);
    }

    [Fact]
    public void 既存_remote_reader_endpoint_updateでtype_nameが空ならlocal_writerをunmatchする()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;
        using var pubA = pA.CreatePublisher<StringMessage>(
            "type_update", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);
        var userWriter = GetUserWriter(pubA)!;
        var remotePrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x20, 0x30, 0x02);
        var remoteParticipantGuid = new Guid(remotePrefix, EntityId.Participant);
        var remoteReaderGuid = new Guid(remotePrefix, new EntityId(0x41u, EntityKind.UserDefinedReaderNoKey));
        SeedRemoteParticipant(pA.DiscoveryDb, remotePrefix);

        pA.DiscoveryDb.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = remoteParticipantGuid,
            TopicName = "rt/type_update",
            TypeName = StringMessage.DdsTypeName,
        }, DateTime.UtcNow);
        userWriter.GetReaderProxy(remoteReaderGuid).Should().NotBeNull();

        pA.DiscoveryDb.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = remoteParticipantGuid,
            TopicName = "rt/type_update",
            TypeName = "",
        }, DateTime.UtcNow);

        userWriter.GetReaderProxy(remoteReaderGuid).Should().BeNull();
    }

    [Fact]
    public async Task participant_lease失効でremote_endpointとlocal_matchingを解除する()
    {
        var env = CreatePair(Duration.FromSeconds(0.15));
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;
        using var subB = pB.CreateSubscription<StringMessage>(
            "lease_topic",
            StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        using var pubA = pA.CreatePublisher<StringMessage>(
            "lease_topic", StringMessageSerializer.Instance, typeName: StringMessage.DdsTypeName);
        var userWriter = GetUserWriter(pubA)!;

        await WaitUntilAsync(() =>
            userWriter.MatchedReaders.Any(proxy => proxy.ReaderGuid.Prefix.Equals(pB.GuidPrefix)),
            because: "lease 失効前は remote reader と match しているべき");

        pB.Stop();

        await WaitUntilAsync(() =>
            !userWriter.MatchedReaders.Any(proxy => proxy.ReaderGuid.Prefix.Equals(pB.GuidPrefix)),
            because: "participant lease 失効時に remote reader lost が local writer へ伝播するべき");
        pA.DiscoveryDb.ReaderSnapshot()
            .Should()
            .NotContain(endpoint => endpoint.Guid.Prefix.Equals(pB.GuidPrefix));
        pA.DiscoveryDb.Count.Should().Be(0);
    }

    private static SedpEndpointWriter? GetSedpPublicationsWriter(DomainParticipant p)
    {
        // DomainParticipant は Context に委譲している。
        // リフレクションで DomainParticipant._context → Context._sedpPublicationsWriter を取得する。
        var ctxField = typeof(DomainParticipant).GetField("_context",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var ctx = ctxField?.GetValue(p);
        if (ctx == null) return null;
        var writerField = ctx.GetType().GetField("_sedpPublicationsWriter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return writerField?.GetValue(ctx) as SedpEndpointWriter;
    }

    private static StatefulWriter? GetUserWriter<T>(Publisher<T> publisher)
    {
        var field = typeof(Publisher<T>).GetField("_writer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(publisher) as StatefulWriter;
    }

    private static void SeedRemoteParticipant(DiscoveryDb db, GuidPrefix remotePrefix)
    {
        db.UpsertParticipant(new ParticipantData
        {
            Guid = new Guid(remotePrefix, EntityId.Participant),
            LeaseDuration = Duration.FromSeconds(20),
        }, DateTime.UtcNow);
    }

    private sealed class SedpEndpointWireFixture : IDisposable
    {
        public GuidPrefix WriterPrefix { get; } = GuidPrefix.Create(VendorId.ROSettaDDS, 0x30, 0x40, 0x01);
        public GuidPrefix ReaderPrefix { get; } = GuidPrefix.Create(VendorId.ROSettaDDS, 0x30, 0x40, 0x02);
        public Locator WriterLocator { get; } = Locator.FromUdpV4(IPAddress.Parse("10.0.0.30"), 7431u);
        public Locator ReaderLocator { get; } = Locator.FromUdpV4(IPAddress.Parse("10.0.0.31"), 7433u);
        public LoopbackTransport WriterTransport { get; }
        public LoopbackTransport ReaderTransport { get; }
        public DiscoveryDb DiscoveryDb { get; } = new();
        public SedpEndpointWriter Writer { get; }
        public SedpEndpointReader Reader { get; }

        private readonly LoopbackHub _hub = new();

        public SedpEndpointWireFixture()
        {
            WriterTransport = _hub.Create(WriterLocator);
            ReaderTransport = _hub.Create(ReaderLocator);
            SeedRemoteParticipant(DiscoveryDb, WriterPrefix);
            Writer = new SedpEndpointWriter(
                transport: WriterTransport,
                multicastDestination: ReaderLocator,
                version: ProtocolVersion.V2_4,
                vendorId: VendorId.ROSettaDDS,
                localPrefix: WriterPrefix,
                writerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsWriter,
                heartbeatPeriod: TimeSpan.FromMilliseconds(50));
            Reader = new SedpEndpointReader(
                replyTransport: ReaderTransport,
                discoveryDb: DiscoveryDb,
                version: ProtocolVersion.V2_4,
                vendorId: VendorId.ROSettaDDS,
                localPrefix: ReaderPrefix,
                readerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsReader,
                ackNackFallbackDestination: WriterLocator,
                producedEndpointKind: EndpointKind.Writer);

            WriterTransport.Received += Writer.OnPacketReceived;
            ReaderTransport.Received += Reader.OnPacketReceived;
        }

        public void Match()
        {
            Reader.MatchRemoteWriter(Writer.Guid, WriterLocator);
            Writer.MatchRemoteReader(Reader.Guid, ReaderLocator);
        }

        public void Dispose()
        {
            WriterTransport.Received -= Writer.OnPacketReceived;
            ReaderTransport.Received -= Reader.OnPacketReceived;
            Reader.Dispose();
            Writer.Dispose();
            ReaderTransport.Dispose();
            WriterTransport.Dispose();
        }
    }

    private static DiscoveredEndpointData CreateWriterEndpoint(
        GuidPrefix participantPrefix,
        EntityId entityId,
        string topicName,
        string typeName)
    {
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = new Guid(participantPrefix, entityId),
            ParticipantGuid = new Guid(participantPrefix, EntityId.Participant),
            TopicName = topicName,
            TypeName = typeName,
        };
        endpoint.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Parse("10.0.0.32"), 7441u));
        return endpoint;
    }

    private static CacheChange SingleHistoryChange(SedpEndpointWriter writer)
    {
        var history = writer.Stateful.History;
        var changes = history.EnumerateRange(history.FirstSequenceNumber, history.LastSequenceNumber);
        changes.Should().ContainSingle();
        return changes[0];
    }

    private static DiscoveredEndpointData ReadEndpointPayload(CacheChange change)
    {
        var payload = change.SerializedPayload.Span;
        var (encapsulation, _) = CdrEncapsulation.Read(payload[..CdrEncapsulation.Size]);
        var reader = new CdrReader(
            payload,
            CdrEncapsulation.GetEndianness(encapsulation),
            cdrOrigin: CdrEncapsulation.Size);
        return DiscoveredEndpointDataSerializer.Read(ref reader, EndpointKind.Writer);
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
