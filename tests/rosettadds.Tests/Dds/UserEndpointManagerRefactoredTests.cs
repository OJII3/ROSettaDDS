using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class UserEndpointManagerRefactoredTests
{
    private static readonly GuidPrefix s_prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
    private static int s_writerCounter;

    private sealed class ThrowingEndpointReceiver : IEndpointReceiver
    {
        public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
            => throw new InvalidOperationException("simulated writer registration failure");
        public void UnregisterWriter(EntityId writerEntityId) { }
        public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
            => throw new InvalidOperationException("simulated reader registration failure");
        public void UnregisterReader(EntityId readerEntityId) { }
    }

    private sealed class RecordingTransport : IRtpsTransport
    {
        public Locator LocalLocator => Locator.FromUdpV4(IPAddress.Loopback, 7411);

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

    private static StatefulWriter CreateWriter(string topic, out DiscoveredEndpointData endpointData, out Guid writerGuid)
    {
        writerGuid = new Guid(s_prefix, new EntityId((uint)Interlocked.Increment(ref s_writerCounter), EntityKind.UserDefinedWriterNoKey));
        var transport = new RecordingTransport();
        var history = new WriterHistoryCache(writerGuid);
        var writer = new StatefulWriter(
            transport,
            Locator.FromUdpV4(IPAddress.Loopback, 7401),
            ProtocolVersion.Current,
            VendorId.ROSettaDDS,
            s_prefix,
            writerGuid.EntityId,
            TimeSpan.FromSeconds(1),
            history,
            NullLogger.Instance);
        endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        return writer;
    }

    private static StatefulWriter CreateWriterWithGuid(Guid guid, string topic, out DiscoveredEndpointData endpointData)
    {
        var transport = new RecordingTransport();
        var history = new WriterHistoryCache(guid);
        var writer = new StatefulWriter(
            transport,
            Locator.FromUdpV4(IPAddress.Loopback, 7401),
            ProtocolVersion.Current,
            VendorId.ROSettaDDS,
            guid.Prefix,
            guid.EntityId,
            TimeSpan.FromSeconds(1),
            history,
            NullLogger.Instance);
        endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = guid,
            ParticipantGuid = new Guid(guid.Prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 7411));
        return writer;
    }

    [Fact]
    public void RegisterWriter_は_receiver_RegisterWriter_を呼び_writerSnapshotに含める()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/chatter", out var endpointData, out _);

        manager.RegisterWriter(endpointData, writer);

        receiver.RegisteredWriters.Should().Contain(e => e.entityId == writer.WriterEntityId);
        manager.Snapshot().Writers.Should().Contain(writer);
    }

    [Fact]
    public void RegisterWriter_は_RemoteReader_snapshotの同topicとmatchする()
    {
        var remotePrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9);
        var remoteReaderGuid = new Guid(remotePrefix, new EntityId(100, EntityKind.UserDefinedReaderNoKey));
        var discovery = new DiscoveryDb();
        discovery.UpsertParticipant(new ParticipantData
        {
            Guid = new Guid(remotePrefix, EntityId.Participant),
            ProtocolVersion = ProtocolVersion.Current,
            VendorId = VendorId.ROSettaDDS,
        }, DateTime.UtcNow);
        var remoteReaderData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = new Guid(remotePrefix, EntityId.Participant),
            TopicName = "rt/chatter",
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        remoteReaderData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 9999));
        discovery.UpsertEndpoint(remoteReaderData, DateTime.UtcNow);

        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(discovery, receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/chatter", out var endpointData, out _);

        manager.RegisterWriter(endpointData, writer);

        writer.MatchedReaders.Should().ContainSingle(r => r.ReaderGuid == remoteReaderGuid);
    }

    [Fact]
    public void UnregisterWriter_は_not_found時に_NotFoundを返す()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/chatter", out _, out _);
        var unknownGuid = new Guid(s_prefix, new EntityId(999, EntityKind.UserDefinedWriterNoKey));

        var result = manager.UnregisterWriter(unknownGuid, writer);

        result.Should().Be(UserEndpointManager.UnregisterResult.NotFound);
    }

    [Fact]
    public void UnregisterWriter_は_最後の1個なら_shouldAdvertise_trueを返す()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/chatter", out var endpointData, out var writerGuid);

        manager.RegisterWriter(endpointData, writer);
        var result = manager.UnregisterWriter(writerGuid, writer);

        result.Endpoint.Should().NotBeNull();
        result.ShouldAdvertise.Should().BeTrue();
    }

    [Fact]
    public void UnregisterWriter_は_他topicに異なるGUID残存なら_shouldAdvertise_trueを返す()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer1 = CreateWriter("rt/chatter", out var ep1, out var guid1);
        var writer2 = CreateWriter("rt/chatter", out var ep2, out var guid2);

        manager.RegisterWriter(ep1, writer1);
        manager.RegisterWriter(ep2, writer2);

        var result = manager.UnregisterWriter(guid1, writer1);

        result.Endpoint.Should().NotBeNull();
        result.ShouldAdvertise.Should().BeTrue();
    }

    [Fact]
    public void Phase2_例外時に_手動rollback_paternで解放できる()
    {
        var receiver = new ThrowingEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/rollback", out var endpointData, out var writerGuid);

        manager.RegisterWriterMetadata(endpointData, writer);
        Assert.Throws<InvalidOperationException>(() =>
            manager.CompleteWriterRegistration(endpointData, writer));

        var result = manager.UnregisterWriterMetadata(writerGuid, writer);
        if (result.Endpoint is not null)
        {
            manager.CompleteWriterUnregistration(writerGuid, writer, result);
        }
        manager.Snapshot().Writers.Should().BeEmpty();
    }

    [Fact]
    public void RemoteReaderChanged_は_同topicの_local_writerと_matchする()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/chatter", out var endpointData, out _);
        manager.RegisterWriter(endpointData, writer);

        var remoteReaderGuid = new Guid(s_prefix, new EntityId(100, EntityKind.UserDefinedReaderNoKey));
        var remoteReaderData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = remoteReaderGuid,
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = "rt/chatter",
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        remoteReaderData.UnicastLocators.Add(Locator.FromUdpV4(IPAddress.Loopback, 9999));
        var remoteReader = new RemoteEndpoint(remoteReaderData, DateTime.UtcNow);

        manager.RemoteReaderChanged(remoteReader);

        writer.MatchedReaders.Should().ContainSingle(r => r.ReaderGuid == remoteReaderGuid);
    }
}
