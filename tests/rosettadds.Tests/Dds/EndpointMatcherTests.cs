using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class EndpointMatcherTests
{
    private sealed class RecordingTransport : IRtpsTransport
    {
        public RecordingTransport(uint port)
        {
            LocalLocator = Locator.FromUdpV4(IPAddress.Loopback, port);
        }

        public Locator LocalLocator { get; }

        public event Action<ReadOnlyMemory<byte>, Locator>? Received
        {
            add { }
            remove { }
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> packet,
            Locator destination,
            CancellationToken cancellationToken = default)
            => default;

        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static readonly GuidPrefix s_prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);

    private static LocalWriter MakeLocalWriter(string topic, string typeName)
    {
        var transport = new RecordingTransport(7411);
        var writerGuid = new Guid(s_prefix, new EntityId(1, EntityKind.UserDefinedWriterNoKey));
        var history = new WriterHistoryCache(writerGuid);
        var statefulWriter = new StatefulWriter(
            transport,
            Locator.FromUdpV4(IPAddress.Loopback, 7401),
            ProtocolVersion.Current,
            VendorId.ROSettaDDS,
            s_prefix,
            writerGuid.EntityId,
            TimeSpan.FromSeconds(1),
            history,
            NullLogger.Instance);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        return new LocalWriter(endpointData, statefulWriter);
    }

    private static LocalReader MakeLocalReader(string topic, string typeName)
    {
        var readerEntityId = new EntityId(2, EntityKind.UserDefinedReaderNoKey);
        var reader = new BestEffortUserReader(
            s_prefix, readerEntityId, NullLogger.Instance, DataFragReassemblyOptions.Default);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(s_prefix, readerEntityId),
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7412));
        return new LocalReader(endpointData, reader);
    }

    private static RemoteEndpoint MakeRemoteReader(
        string topic, string typeName, Locator? unicast = null, GuidPrefix? prefix = null)
    {
        var p = prefix ?? GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9);
        var endpointGuid = new Guid(p, new EntityId(100, EntityKind.UserDefinedReaderNoKey));
        var data = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = endpointGuid,
            ParticipantGuid = new Guid(p, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        if (unicast.HasValue)
        {
            data.UnicastLocators.Add(unicast.Value);
        }
        return new RemoteEndpoint(data, DateTime.UtcNow);
    }

    private static RemoteEndpoint MakeRemoteWriter(
        string topic, string typeName, Locator? unicast = null, GuidPrefix? prefix = null)
    {
        var p = prefix ?? GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9);
        var endpointGuid = new Guid(p, new EntityId(100, EntityKind.UserDefinedWriterNoKey));
        var data = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = endpointGuid,
            ParticipantGuid = new Guid(p, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        if (unicast.HasValue)
        {
            data.UnicastLocators.Add(unicast.Value);
        }
        return new RemoteEndpoint(data, DateTime.UtcNow);
    }

    private static RemoteParticipant MakeRemoteParticipant(GuidPrefix prefix, params Locator[] defaultUnicast)
    {
        var data = new ParticipantData
        {
            Guid = new Guid(prefix, EntityId.Participant),
            ProtocolVersion = ProtocolVersion.Current,
            VendorId = VendorId.ROSettaDDS,
        };
        foreach (var loc in defaultUnicast)
        {
            data.DefaultUnicastLocators.Add(loc);
        }
        return new RemoteParticipant(data, DateTime.UtcNow);
    }

    [Fact]
    public void TypeMatches_は両方非空で一致ならtrueを返す()
    {
        EndpointMatcher.TypeMatches("foo", "foo").Should().BeTrue();
    }

    [Theory]
    [InlineData("", "foo")]
    [InlineData("foo", "")]
    public void TypeMatches_は空文字を含むとfalseを返す(string a, string b)
    {
        EndpointMatcher.TypeMatches(a, b).Should().BeFalse();
    }

    [Fact]
    public void FirstUdpLocator_はUDPv4を返す()
    {
        var loc = Locator.FromUdpV4(IPAddress.Loopback, 7411);
        var result = EndpointMatcher.FirstUdpLocator(new[] { loc });
        result.Should().Be(loc);
    }

    [Fact]
    public void FirstUdpLocator_はUDPv6も許容する()
    {
        var loc = Locator.FromUdpV6(IPAddress.IPv6Loopback, 7411);
        var result = EndpointMatcher.FirstUdpLocator(new[] { loc });
        result.Should().Be(loc);
    }

    [Fact]
    public void FirstUdpLocator_は非UDPをスキップする()
    {
        Span<byte> zero = stackalloc byte[16];
        var nonUdp = new Locator(LocatorKind.TcpV4, 7411, zero);
        var udp = Locator.FromUdpV4(IPAddress.Loopback, 7412);
        var result = EndpointMatcher.FirstUdpLocator(new[] { nonUdp, udp });
        result.Should().Be(udp);
    }

    [Fact]
    public void FirstUdpLocator_は空入力でnullを返す()
    {
        EndpointMatcher.FirstUdpLocator(Array.Empty<Locator>()).Should().BeNull();
    }

    [Fact]
    public void EvaluateLocalRemote_Writer_は_TypeName不一致でCompatible_falseを返す()
    {
        var local = MakeLocalWriter("rt/chatter", "TypeA");
        var remote = MakeRemoteReader("rt/chatter", "TypeB");
        var result = EndpointMatcher.EvaluateLocalRemote(local, remote);
        result.IsCompatible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateLocalRemote_Writer_は_互換QoSでCompatible_trueとlocatorを返す()
    {
        var remoteLoc = Locator.FromUdpV4(IPAddress.Loopback, 9999);
        var local = MakeLocalWriter("rt/chatter", "TypeA");
        var remote = MakeRemoteReader("rt/chatter", "TypeA", unicast: remoteLoc);
        var result = EndpointMatcher.EvaluateLocalRemote(local, remote);
        result.IsCompatible.Should().BeTrue();
        result.UnicastLocator.Should().Be(remoteLoc);
        result.ReliabilityKind.Should().Be(ReliabilityKind.BestEffort);
    }

    [Fact]
    public void EvaluateLocalLocal_は_TypeName不一致で両方向unmatch指示()
    {
        var localReader = MakeLocalReader("rt/chatter", "TypeA");
        var localWriter = MakeLocalWriter("rt/chatter", "TypeB");
        var result = EndpointMatcher.EvaluateLocalLocal(localReader, localWriter);
        result.IsCompatible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateLocalRemote_Reader_は_TypeName不一致でCompatible_falseを返す()
    {
        var local = MakeLocalReader("t", "TypeA");
        var remote = MakeRemoteWriter("t", "TypeB");

        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);

        d.IsCompatible.Should().BeFalse();
        d.UnicastLocator.Should().BeNull();
    }

    [Fact]
    public void EvaluateLocalRemote_Reader_は_互換QoSでCompatible_trueとlocatorを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var locator = Locator.FromUdpV4(IPAddress.Loopback, 7411);
        var local = MakeLocalReader("t", "TypeA");
        var remote = MakeRemoteWriter("t", "TypeA", unicast: locator, prefix: prefix);

        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);

        d.IsCompatible.Should().BeTrue();
        d.UnicastLocator.Should().Be(locator);
        d.ReliabilityKind.Should().BeNull();
    }

    [Fact]
    public void EvaluateLocalLocal_は_互換時に両方向のlocatorを両方返す()
    {
        var reader = MakeLocalReader("rt/chatter", "TypeA");
        var writer = MakeLocalWriter("rt/chatter", "TypeA");
        var result = EndpointMatcher.EvaluateLocalLocal(reader, writer);
        result.IsCompatible.Should().BeTrue();
        result.UnicastLocator.Should().Be(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        result.SecondaryLocator.Should().Be(Locator.FromUdpV4(IPAddress.Loopback, 7412));
        result.ReliabilityKind.Should().Be(ReliabilityKind.BestEffort);
    }

    [Fact]
    public void EvaluateLocalLocal_は_writer_のみ_locator_持ち_reader_null_locator_を許容()
    {
        var writer = MakeLocalWriter("rt/chatter", "TypeA");
        var reader = MakeLocalReader("rt/chatter", "TypeA");
        reader.EndpointData.UnicastLocators.Clear();
        var result = EndpointMatcher.EvaluateLocalLocal(reader, writer);
        result.IsCompatible.Should().BeTrue();
        result.UnicastLocator.Should().Be(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        result.SecondaryLocator.Should().BeNull();
        result.ReliabilityKind.Should().Be(ReliabilityKind.BestEffort);
    }

    [Fact]
    public void EvaluateLocalRemote_Writer_は_互換時に_remote_の_reliability_kind_を返す()
    {
        var remoteLoc = Locator.FromUdpV4(IPAddress.Loopback, 9999);
        var local = MakeLocalWriter("rt/chatter", "TypeA");
        var remote = MakeRemoteReader("rt/chatter", "TypeA", unicast: remoteLoc);
        local.EndpointData.Reliability = ReliabilityQos.Reliable;
        var result = EndpointMatcher.EvaluateLocalRemote(local, remote);
        result.IsCompatible.Should().BeTrue();
        result.UnicastLocator.Should().Be(remoteLoc);
        result.ReliabilityKind.Should().Be(ReliabilityKind.BestEffort);
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_endpoint直指定のlocatorを優先する()
    {
        var epLoc = Locator.FromUdpV4(IPAddress.Loopback, 9999);
        var remote = MakeRemoteReader("rt/chatter", "TypeA", unicast: epLoc);
        var result = EndpointMatcher.ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>());
        result.Should().Be(epLoc);
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_endpoint_locator無しなら_participant_defaultにフォールバックする()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9);
        var participantLoc = Locator.FromUdpV4(IPAddress.Loopback, 8888);
        var remote = MakeRemoteReader("rt/chatter", "TypeA", unicast: null, prefix: prefix);
        var participant = MakeRemoteParticipant(prefix, participantLoc);
        var result = EndpointMatcher.ResolveRemoteUnicastLocator(remote, new[] { participant });
        result.Should().Be(participantLoc);
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_解決不可ならnullを返す()
    {
        var remote = MakeRemoteReader("rt/chatter", "TypeA");
        var result = EndpointMatcher.ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>());
        result.Should().BeNull();
    }
}
