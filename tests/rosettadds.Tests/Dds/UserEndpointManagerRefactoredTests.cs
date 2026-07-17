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

    private sealed class RecordingThrowingEndpointReceiver : IEndpointReceiver
    {
        public List<EntityId> RegisteredWriters { get; } = new();
        public List<EntityId> RegisteredReaders { get; } = new();
        public List<EntityId> UnregisteredWriters { get; } = new();
        public List<EntityId> UnregisteredReaders { get; } = new();

        public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
        {
            RegisteredWriters.Add(writerEntityId);
            throw new InvalidOperationException("simulated Phase 2 writer registration failure");
        }

        public void UnregisterWriter(EntityId writerEntityId)
            => UnregisteredWriters.Add(writerEntityId);

        public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
        {
            RegisteredReaders.Add(readerEntityId);
            throw new InvalidOperationException("simulated Phase 2 reader registration failure");
        }

        public void UnregisterReader(EntityId readerEntityId)
            => UnregisteredReaders.Add(readerEntityId);
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
    public void Normal_RegisterUnregister_writerは同じEntityIdでpairingする()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/pairing", out var endpointData, out var writerGuid);
        var writerEntityId = writer.WriterEntityId;

        manager.RegisterWriter(endpointData, writer);
        receiver.RegisteredWriters.Should().Contain(e => e.entityId == writerEntityId);

        manager.UnregisterWriter(writerGuid, writer);
        receiver.UnregisteredWriters.Should().Contain(writerEntityId);
    }

    [Fact]
    public void Normal_RegisterUnregister_readerは同じEntityIdでpairingする()
    {
        var receiver = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var topic = "rt/reader_pairing";
        var guid = new Guid(s_prefix, new EntityId(200, EntityKind.UserDefinedReaderNoKey));
        var userReader = new BestEffortUserReader(
            s_prefix, guid.EntityId, NullLogger.Instance, DataFragReassemblyOptions.Default);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = guid,
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(System.Net.IPAddress.Loopback, 7411));

        manager.RegisterReader(endpointData, userReader);
        receiver.RegisteredReaders.Should().Contain(e => e.entityId == guid.EntityId);

        manager.UnregisterReader(guid, userReader);
        receiver.UnregisteredReaders.Should().Contain(guid.EntityId);
    }

    [Fact]
    public void Phase2_writer_registration_failure_は_rollbackでUnregisterWriterを呼ぶ()
    {
        var receiver = new RecordingThrowingEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var writer = CreateWriter("rt/rollback_writer", out var endpointData, out var writerGuid);
        var writerEntityId = writer.WriterEntityId;

        manager.RegisterWriterMetadata(endpointData, writer);
        Assert.Throws<InvalidOperationException>(() =>
            manager.CompleteWriterRegistration(endpointData, writer));

        // Phase 2 は receiver 到達後に失敗している
        receiver.RegisteredWriters.Should().Contain(writerEntityId,
            "Phase 2 failure occurred after reaching receiver");

        // Node の catch ブロックと同じ手動 rollback
        var result = manager.UnregisterWriterMetadata(writerGuid, writer);
        if (result.Endpoint is not null)
        {
            manager.CompleteWriterUnregistration(writerGuid, writer, result);
        }

        // 同じ EntityId が unregister されている
        receiver.UnregisteredWriters.Should().Contain(writerEntityId);
        manager.Snapshot().Writers.Should().BeEmpty();
    }

    [Fact]
    public void Phase2_reader_registration_failure_は_rollbackでUnregisterReaderを呼ぶ()
    {
        var receiver = new RecordingThrowingEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), receiver, NullLogger.Instance);
        var topic = "rt/rollback_reader";
        var guid = new Guid(s_prefix, new EntityId(300, EntityKind.UserDefinedReaderNoKey));
        var userReader = new BestEffortUserReader(
            s_prefix, guid.EntityId, NullLogger.Instance, DataFragReassemblyOptions.Default);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = guid,
            ParticipantGuid = new Guid(s_prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = "TypeA",
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(Locator.FromUdpV4(System.Net.IPAddress.Loopback, 7411));

        manager.RegisterReaderMetadata(endpointData, userReader);
        Assert.Throws<InvalidOperationException>(() =>
            manager.CompleteReaderRegistration(endpointData, userReader));

        receiver.RegisteredReaders.Should().Contain(guid.EntityId,
            "Phase 2 failure occurred after reaching receiver");

        var result = manager.UnregisterReaderMetadata(guid, userReader);
        if (result.Endpoint is not null)
        {
            manager.CompleteReaderUnregistration(guid, userReader, result);
        }

        receiver.UnregisteredReaders.Should().Contain(guid.EntityId);
        manager.Snapshot().Readers.Should().BeEmpty();
    }
}
