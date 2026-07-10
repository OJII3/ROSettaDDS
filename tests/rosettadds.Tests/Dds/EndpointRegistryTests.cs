using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class EndpointRegistryTests
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
    private static int s_writerEntityIdCounter;
    private static int s_readerEntityIdCounter;

    private static LocalWriter MakeWriter(string topic, out StatefulWriter writer)
        => MakeWriterWithGuid(new Guid(s_prefix, new EntityId((uint)Interlocked.Increment(ref s_writerEntityIdCounter), EntityKind.UserDefinedWriterNoKey)), topic, out writer);

    private static LocalWriter MakeWriterWithGuid(Guid writerGuid, string topic, out StatefulWriter writer)
    {
        var transport = new RecordingTransport(7411);
        var history = new WriterHistoryCache(writerGuid);
        writer = new StatefulWriter(
            transport,
            Locator.FromUdpV4(IPAddress.Loopback, 7401),
            ProtocolVersion.Current,
            VendorId.ROSettaDDS,
            writerGuid.Prefix,
            writerGuid.EntityId,
            TimeSpan.FromSeconds(1),
            history,
            NullLogger.Instance);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(writerGuid.Prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        return new LocalWriter(endpointData, writer);
    }

    private static LocalReader MakeReader(string topic, out IUserReader reader)
    {
        var entityId = new EntityId((uint)Interlocked.Increment(ref s_readerEntityIdCounter), EntityKind.UserDefinedReaderNoKey);
        reader = new BestEffortUserReader(s_prefix, entityId, NullLogger.Instance);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(s_prefix, entityId),
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7412));
        return new LocalReader(endpointData, reader);
    }

    [Fact]
    public void AddLocalWriter_は_writerSnapshotに含める()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out _);

        registry.AddLocalWriter(lw.EndpointData, lw.Writer);
        var snapshot = registry.Snapshot();

        snapshot.Writers.Should().Contain(lw.Writer);
    }

    [Fact]
    public void AddLocalReader_は_topic別に保持されGetLocalReadersForTopicで取得できる()
    {
        var registry = new EndpointRegistry();
        var lr1 = MakeReader("rt/chatter", out _);
        var lr2 = MakeReader("rt/chatter", out _);
        var lrOther = MakeReader("rt/other", out _);

        registry.AddLocalReader(lr1.EndpointData, lr1.Reader);
        registry.AddLocalReader(lr2.EndpointData, lr2.Reader);
        registry.AddLocalReader(lrOther.EndpointData, lrOther.Reader);

        var forChatter = registry.GetLocalReadersForTopic("rt/chatter");
        forChatter.Should().HaveCount(2);
        forChatter.Select(r => r.Reader).Should().Contain(lr1.Reader);
        forChatter.Select(r => r.Reader).Should().Contain(lr2.Reader);
    }

    [Fact]
    public void RemoveLocalWriter_は_他端のLocalReader配列を返す()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out var writer);
        var lr = MakeReader("rt/chatter", out _);
        registry.AddLocalWriter(lw.EndpointData, writer);
        registry.AddLocalReader(lr.EndpointData, lr.Reader);

        var removed = registry.RemoveLocalWriter(lw.EndpointData.EndpointGuid, writer);

        removed.Endpoint.Should().NotBeNull();
        removed.Endpoint!.EndpointGuid.Should().Be(lw.EndpointData.EndpointGuid);
        removed.LocalReaders.Should().HaveCount(1);
        removed.LocalReaders.Select(r => r.Reader).Should().Contain(lr.Reader);
    }

    [Fact]
    public void RemoveLocalWriter_は_GUID一致なしなら_endpoint_nullと空配列を返す()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out var writer);
        registry.AddLocalWriter(lw.EndpointData, writer);

        var otherGuid = new Guid(s_prefix, new EntityId(999, EntityKind.UserDefinedWriterNoKey));
        var removed = registry.RemoveLocalWriter(otherGuid, writer);

        removed.Endpoint.Should().BeNull();
        removed.LocalReaders.Should().BeEmpty();
    }

    [Fact]
    public void ShouldAdvertiseForTopic_は_同じGUIDが他に残っていなければtrueを返す()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out var writer);
        registry.AddLocalWriter(lw.EndpointData, writer);

        registry.RemoveLocalWriter(lw.EndpointData.EndpointGuid, writer);

        // No other entry with the same GUID exists → should advertise
        registry.ShouldAdvertiseForTopic("rt/chatter", lw.EndpointData.EndpointGuid).Should().BeTrue();
    }

    [Fact]
    public void ShouldAdvertiseForTopic_は_同じGUIDが他に残っていればfalseを返す()
    {
        var registry = new EndpointRegistry();
        var prefix = s_prefix;
        var sharedGuid = new Guid(prefix, new EntityId(42, EntityKind.UserDefinedWriterNoKey));

        // Create 2 writer endpoints with the same GUID (defensive guard scenario)
        var lw1 = MakeWriterWithGuid(sharedGuid, "rt/chatter", out var writer1);
        var lw2 = MakeWriterWithGuid(sharedGuid, "rt/chatter", out var writer2);

        registry.AddLocalWriter(lw1.EndpointData, writer1);
        registry.AddLocalWriter(lw2.EndpointData, writer2);

        // Remove the first writer — the second one (same GUID) remains
        registry.RemoveLocalWriter(sharedGuid, writer1);

        // The same GUID is still in the topic map → should NOT advertise
        registry.ShouldAdvertiseForTopic("rt/chatter", sharedGuid).Should().BeFalse();
    }

    [Fact]
    public void StartWriters_と_StopWriters_は_writerSnapshotの全writerに伝播する()
    {
        var registry = new EndpointRegistry();
        var lw1 = MakeWriter("rt/a", out var w1);
        var lw2 = MakeWriter("rt/b", out var w2);
        registry.AddLocalWriter(lw1.EndpointData, w1);
        registry.AddLocalWriter(lw2.EndpointData, w2);

        registry.StartWriters();
        registry.StopWriters();
    }

    [Fact]
    public void RemoveLocalReader_は_他端のLocalWriter配列を返す()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out var writer);
        var lr = MakeReader("rt/chatter", out var reader);
        registry.AddLocalWriter(lw.EndpointData, writer);
        registry.AddLocalReader(lr.EndpointData, reader);

        var removed = registry.RemoveLocalReader(lr.EndpointData.EndpointGuid, reader);

        removed.Endpoint.Should().NotBeNull();
        removed.Endpoint!.EndpointGuid.Should().Be(lr.EndpointData.EndpointGuid);
        removed.LocalWriters.Should().HaveCount(1);
        removed.LocalWriters.Select(w => w.Writer).Should().Contain(writer);
    }

    [Fact]
    public void AddLocalReader_は_Snapshot_の_Readersに含める()
    {
        var registry = new EndpointRegistry();
        var lr = MakeReader("rt/chatter", out var reader);

        registry.AddLocalReader(lr.EndpointData, reader);
        var snapshot = registry.Snapshot();

        snapshot.Readers.Should().Contain(reader);
    }

    [Fact]
    public void ShouldAdvertiseForTopic_は_reader_topic_mapも確認する()
    {
        var registry = new EndpointRegistry();
        var lw = MakeWriter("rt/chatter", out var writer);
        var lr = MakeReader("rt/chatter", out _);
        registry.AddLocalWriter(lw.EndpointData, writer);
        registry.AddLocalReader(lr.EndpointData, lr.Reader);

        registry.RemoveLocalWriter(lw.EndpointData.EndpointGuid, writer);

        // No other writer or reader with the same GUID exists → should advertise
        registry.ShouldAdvertiseForTopic("rt/chatter", lw.EndpointData.EndpointGuid).Should().BeTrue();
    }

    [Fact]
    public void UpdateLocalLocatorsは_writerと_readerを更新して再広告snapshotを返す()
    {
        var registry = new EndpointRegistry();
        var localWriter = MakeWriter("rt/chatter", out var writer);
        var localReader = MakeReader("rt/chatter", out var reader);
        registry.AddLocalWriter(localWriter.EndpointData, writer);
        registry.AddLocalReader(localReader.EndpointData, reader);
        var unicastLocators = new[]
        {
            Locator.FromUdpV4(IPAddress.Parse("192.0.2.10"), 7411),
            Locator.FromUdpV4(IPAddress.Parse("192.0.2.11"), 7411),
        };
        var multicastLocator = Locator.FromUdpV4(IPAddress.Parse("239.255.0.1"), 7401);

        var snapshot = registry.UpdateLocalLocators(unicastLocators, multicastLocator);

        snapshot.Writers.Should().ContainSingle().Which.Should().BeSameAs(localWriter.EndpointData);
        snapshot.Readers.Should().ContainSingle().Which.Should().BeSameAs(localReader.EndpointData);
        localWriter.EndpointData.UnicastLocators.Should().Equal(unicastLocators);
        localWriter.EndpointData.MulticastLocators.Should().Equal(multicastLocator);
        localReader.EndpointData.UnicastLocators.Should().Equal(unicastLocators);
        localReader.EndpointData.MulticastLocators.Should().Equal(multicastLocator);
    }
}
