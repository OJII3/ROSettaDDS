using ROSettaDDS.Common;
using ROSettaDDS.Discovery;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Discovery;

public class DiscoveryDbTests
{
    private static GuidPrefix Prefix(byte id)
        => GuidPrefix.Create(VendorId.ROSettaDDS, id, (uint)(0x1000 + id), (ushort)(0x2000 + id));

    private static ParticipantData Participant(GuidPrefix prefix, double leaseSeconds = 20)
        => new()
        {
            Guid = new Guid(prefix, EntityId.Participant),
            LeaseDuration = Duration.FromSeconds(leaseSeconds),
        };

    private static DiscoveredEndpointData Endpoint(
        GuidPrefix prefix,
        EndpointKind kind,
        uint entityKey,
        string topic = "rt/chatter",
        string type = "std_msgs::msg::dds_::String_")
        => new()
        {
            Kind = kind,
            EndpointGuid = new Guid(
                prefix,
                new EntityId(
                    entityKey,
                    kind == EndpointKind.Writer
                        ? EntityKind.UserDefinedWriterNoKey
                        : EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = type,
        };

    [Fact]
    public void ExpireOldParticipants_は同じparticipant_prefixのendpointを削除してLostを通知する()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x01, 0x02, 0x03);
        var participantGuid = new Guid(prefix, EntityId.Participant);
        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));
        var readerGuid = new Guid(prefix, new EntityId(0x11u, EntityKind.UserDefinedReaderNoKey));
        var events = new List<string>();

        db.WriterLost += endpoint => events.Add($"writer:{endpoint.Guid}");
        db.ReaderLost += endpoint => events.Add($"reader:{endpoint.Guid}");
        db.ParticipantLost += participant => events.Add($"participant:{participant.Guid}");

        db.UpsertParticipant(new ParticipantData
        {
            Guid = participantGuid,
            LeaseDuration = Duration.FromSeconds(1),
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = readerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);

        db.ExpireOldParticipants(now + TimeSpan.FromSeconds(2));

        db.Count.Should().Be(0);
        db.WriterCount.Should().Be(0);
        db.ReaderCount.Should().Be(0);
        events.Should().Equal(
            $"writer:{writerGuid}",
            $"reader:{readerGuid}",
            $"participant:{participantGuid}");
    }

    [Fact]
    public void participant上限到達後の新規participantは保持せず既存更新は許可する()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteParticipants: 1));
        var now = DateTime.UtcNow;
        var first = Participant(Prefix(1));
        var second = Participant(Prefix(2));

        db.UpsertParticipant(first, now);
        db.UpsertParticipant(second, now);
        db.Count.Should().Be(1);
        db.Snapshot()[0].Guid.Should().Be(first.Guid);

        first.EntityName = "updated";
        db.UpsertParticipant(first, now.AddSeconds(1));

        db.Count.Should().Be(1);
        db.Snapshot()[0].Data.EntityName.Should().Be("updated");
    }

    [Fact]
    public void 未知participantに属するendpointは拒否する()
    {
        var db = new DiscoveryDb();

        db.UpsertEndpoint(Endpoint(Prefix(1), EndpointKind.Writer, 0x10), DateTime.UtcNow);

        db.WriterCount.Should().Be(0);
    }

    [Fact]
    public void participant_guidとendpoint_guidのprefixが不一致ならendpointを拒否する()
    {
        var db = new DiscoveryDb();
        var participantPrefix = Prefix(1);
        var endpointPrefix = Prefix(2);
        db.UpsertParticipant(Participant(participantPrefix), DateTime.UtcNow);
        var endpoint = Endpoint(endpointPrefix, EndpointKind.Writer, 0x10);
        endpoint.ParticipantGuid = new Guid(participantPrefix, EntityId.Participant);

        db.UpsertEndpoint(endpoint, DateTime.UtcNow);

        db.WriterCount.Should().Be(0);
    }

    [Fact]
    public void writer上限到達後の新規writerは保持しない()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteWriters: 1));
        var now = DateTime.UtcNow;
        var firstPrefix = Prefix(1);
        var secondPrefix = Prefix(2);
        db.UpsertParticipant(Participant(firstPrefix), now);
        db.UpsertParticipant(Participant(secondPrefix), now);

        db.UpsertEndpoint(Endpoint(firstPrefix, EndpointKind.Writer, 0x10), now);
        db.UpsertEndpoint(Endpoint(secondPrefix, EndpointKind.Writer, 0x11), now);

        db.WriterCount.Should().Be(1);
    }

    [Fact]
    public void participantあたりのendpoint上限到達後は新規endpointを保持しない()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(maxRemoteEndpointsPerParticipant: 1));
        var now = DateTime.UtcNow;
        var prefix = Prefix(1);
        db.UpsertParticipant(Participant(prefix), now);

        db.UpsertEndpoint(Endpoint(prefix, EndpointKind.Writer, 0x10), now);
        db.UpsertEndpoint(Endpoint(prefix, EndpointKind.Reader, 0x11), now);

        db.WriterCount.Should().Be(1);
        db.ReaderCount.Should().Be(0);
    }

    [Fact]
    public void CreateEndpointSnapshot_はwriter_readerを値コピーし変更が影響しない()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x01, 0x02, 0x03);
        var participantGuid = new Guid(prefix, EntityId.Participant);
        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));
        var readerGuid = new Guid(prefix, new EntityId(0x11u, EntityKind.UserDefinedReaderNoKey));

        db.UpsertParticipant(new ParticipantData
        {
            Guid = participantGuid,
            LeaseDuration = Duration.Infinite,
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = readerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/chatter",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);

        var snapshot = db.CreateEndpointSnapshot();

        snapshot.Writers.Should().HaveCount(1);
        snapshot.Readers.Should().HaveCount(1);

        // 各 snapshot 取得で独立したクローンが生成される
        snapshot.Writers[0].TopicName = "rt/modified";

        var snapshot2 = db.CreateEndpointSnapshot();
        snapshot2.Writers[0].TopicName.Should().Be("rt/chatter");
    }

    [Fact]
    public void CreateEndpointSnapshot_は同じロック区間で一貫した集合を返す()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x01, 0x02, 0x03);
        var participantGuid = new Guid(prefix, EntityId.Participant);

        db.UpsertParticipant(new ParticipantData
        {
            Guid = participantGuid,
            LeaseDuration = Duration.Infinite,
        }, now);

        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));
        db.UpsertEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = participantGuid,
            TopicName = "rt/foo",
            TypeName = "std_msgs::msg::dds_::String_",
        }, now);

        // Expire と CreateEndpointSnapshot の競合: 追加前または削除後の一貫した状態になる
        db.ExpireOldParticipants(now + TimeSpan.FromDays(1));
        var snapshot = db.CreateEndpointSnapshot();

        snapshot.Writers.Should().BeEmpty();
        snapshot.Readers.Should().BeEmpty();
    }

    [Fact]
    public void remote_participant_lease_durationは保持前にclampされる()
    {
        var db = new DiscoveryDb(new DiscoveryLimits(
            minRemoteParticipantLeaseSeconds: 1,
            maxRemoteParticipantLeaseSeconds: 2));
        var now = DateTime.UtcNow;
        var prefix = Prefix(1);

        db.UpsertParticipant(Participant(prefix, leaseSeconds: 100), now);

        db.Snapshot()[0].Data.LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        db.UpsertParticipant(Participant(prefix, leaseSeconds: 0.1), now.AddSeconds(1));

        db.Snapshot()[0].Data.LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void CreateEndpointSnapshot保持中はUpsertEndpointがDiscoveryDbの_lock待ちになる()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = Prefix(30);
        var participantGuid = new Guid(prefix, EntityId.Participant);
        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));

        db.UpsertParticipant(Participant(prefix), now);

        var snapshotHoldsLock = new ManualResetEventSlim();
        var snapshotContinue = new ManualResetEventSlim();
        db.SnapshotLockAcquiredCallback = () =>
        {
            snapshotHoldsLock.Set();
            if (!snapshotContinue.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("snapshot hold lock timed out");
        };

        var mutationReachedLockGate = new ManualResetEventSlim();
        var mutationAcquiredLock = new ManualResetEventSlim();
        var mutationCompleted = new ManualResetEventSlim();

        db.ExternalLockEnter = () => mutationReachedLockGate.Set();
        db.MutationLockAcquiredCallback = () => mutationAcquiredLock.Set();

        var snapshotDone = new ManualResetEventSlim();
        Exception? snapshotError = null;
        var snapshotThread = new Thread(() =>
        {
            try
            {
                db.CreateEndpointSnapshot();
            }
            catch (Exception ex)
            {
                snapshotError = ex;
            }
            finally
            {
                snapshotDone.Set();
            }
        });
        snapshotThread.Start();

        Assert.True(snapshotHoldsLock.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must acquire _lock");

        Exception? mutationError = null;
        var mutationThread = new Thread(() =>
        {
            try
            {
                db.UpsertEndpoint(
                    new DiscoveredEndpointData
                    {
                        Kind = EndpointKind.Writer,
                        EndpointGuid = writerGuid,
                        ParticipantGuid = participantGuid,
                        TopicName = "rt/race",
                        TypeName = "std_msgs::msg::dds_::String_",
                    },
                    DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }
            finally
            {
                mutationCompleted.Set();
            }
        });
        mutationThread.Start();

        Assert.True(mutationReachedLockGate.Wait(TimeSpan.FromSeconds(5)),
            "mutation must reach ExternalLockEnter");

        Assert.False(mutationCompleted.Wait(TimeSpan.FromMilliseconds(300)),
            "mutation must be blocked on _lock while snapshot holds it");
        Assert.False(mutationAcquiredLock.IsSet,
            "mutation must not acquire _lock while snapshot holds it");

        snapshotContinue.Set();
        Assert.True(snapshotDone.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must complete after barrier released");

        if (snapshotError is not null)
            throw new Exception("snapshot thread failed", snapshotError);

        Assert.True(mutationCompleted.Wait(TimeSpan.FromSeconds(5)),
            "mutation must complete after snapshot releases _lock");
        Assert.True(mutationAcquiredLock.Wait(TimeSpan.FromSeconds(5)),
            "mutation must acquire _lock after snapshot releases it");

        mutationThread.Join();
        if (mutationError is not null)
            throw new Exception("mutation thread failed", mutationError);

        db.SnapshotLockAcquiredCallback = null;
        db.MutationLockAcquiredCallback = null;
        db.ExternalLockEnter = null;
    }

    [Fact]
    public void CreateEndpointSnapshot保持中はTryRemoveEndpointがDiscoveryDbの_lock待ちになる()
    {
        var db = new DiscoveryDb();
        var now = DateTime.UtcNow;
        var prefix = Prefix(31);
        var participantGuid = new Guid(prefix, EntityId.Participant);
        var writerGuid = new Guid(prefix, new EntityId(0x10u, EntityKind.UserDefinedWriterNoKey));

        db.UpsertParticipant(Participant(prefix), now);
        db.UpsertEndpoint(
            new DiscoveredEndpointData
            {
                Kind = EndpointKind.Writer,
                EndpointGuid = writerGuid,
                ParticipantGuid = participantGuid,
                TopicName = "rt/remove_race",
                TypeName = "std_msgs::msg::dds_::String_",
            },
            now);

        var snapshotHoldsLock = new ManualResetEventSlim();
        var snapshotContinue = new ManualResetEventSlim();
        db.SnapshotLockAcquiredCallback = () =>
        {
            snapshotHoldsLock.Set();
            if (!snapshotContinue.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("snapshot hold lock timed out");
        };

        var mutationReachedLockGate = new ManualResetEventSlim();
        var mutationAcquiredLock = new ManualResetEventSlim();
        var mutationCompleted = new ManualResetEventSlim();

        db.ExternalLockEnter = () => mutationReachedLockGate.Set();
        db.MutationLockAcquiredCallback = () => mutationAcquiredLock.Set();

        var snapshotDone = new ManualResetEventSlim();
        Exception? snapshotError = null;
        var snapshotThread = new Thread(() =>
        {
            try
            {
                db.CreateEndpointSnapshot();
            }
            catch (Exception ex)
            {
                snapshotError = ex;
            }
            finally
            {
                snapshotDone.Set();
            }
        });
        snapshotThread.Start();

        Assert.True(snapshotHoldsLock.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must acquire _lock");

        Exception? mutationError = null;
        var mutationThread = new Thread(() =>
        {
            try
            {
                db.TryRemoveEndpoint(EndpointKind.Writer, writerGuid);
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }
            finally
            {
                mutationCompleted.Set();
            }
        });
        mutationThread.Start();

        Assert.True(mutationReachedLockGate.Wait(TimeSpan.FromSeconds(5)),
            "mutation must reach ExternalLockEnter");

        Assert.False(mutationCompleted.Wait(TimeSpan.FromMilliseconds(300)),
            "mutation must be blocked on _lock while snapshot holds it");
        Assert.False(mutationAcquiredLock.IsSet,
            "mutation must not acquire _lock while snapshot holds it");

        snapshotContinue.Set();
        Assert.True(snapshotDone.Wait(TimeSpan.FromSeconds(5)),
            "snapshot must complete after barrier released");

        if (snapshotError is not null)
            throw new Exception("snapshot thread failed", snapshotError);

        Assert.True(mutationCompleted.Wait(TimeSpan.FromSeconds(5)),
            "mutation must complete after snapshot releases _lock");
        Assert.True(mutationAcquiredLock.Wait(TimeSpan.FromSeconds(5)),
            "mutation must acquire _lock after snapshot releases it");

        mutationThread.Join();
        if (mutationError is not null)
            throw new Exception("mutation thread failed", mutationError);

        db.SnapshotLockAcquiredCallback = null;
        db.MutationLockAcquiredCallback = null;
        db.ExternalLockEnter = null;
    }
}
