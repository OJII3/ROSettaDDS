using System.Buffers;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.HistoryCache;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps.HistoryCache;

public class WriterHistoryCacheOwnedPayloadTests
{
    private static readonly Guid TestGuid = new(
        GuidPrefix.Unknown, EntityId.Unknown);

    [Fact]
    public void Add_with_owner_は_CacheChange_に_owner_を_保持する()
    {
        using var cache = new WriterHistoryCache(TestGuid);
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        var payload = buffer.AsMemory(0, 16);

        var change = cache.Add(ChangeKind.Alive, payload, owner, Time.Now());

        change.PayloadOwner.Should().BeSameAs(owner,
            "CacheChange に渡した owner がそのまま保持されるべき");
        // owner は cache 経由で release される
    }

    [Fact]
    public void RemoveBelowOrEqual_は_該当_owner_を_dispose_する()
    {
        using var cache = new WriterHistoryCache(TestGuid);
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        cache.Add(ChangeKind.Alive, buffer.AsMemory(0, 16), owner, Time.Now());

        cache.RemoveBelowOrEqual(new SequenceNumber(1L));

        // Dispose 後は Buffer 取得で例外
        Action act = () => _ = owner.Buffer;
        act.Should().Throw<ObjectDisposedException>(
            "RemoveBelowOrEqual で owner が dispose されるべき");
    }

    [Fact]
    public void EvictIfNeeded_は_MaxSamples_超過分の_owner_を_dispose_する()
    {
        using var cache = new WriterHistoryCache(TestGuid, maxSamples: 2);
        var owners = new RtpsPayloadOwner[3];
        for (int i = 0; i < 3; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64);
            owners[i] = new RtpsPayloadOwner(buffer);
            cache.Add(ChangeKind.Alive, buffer.AsMemory(0, 16), owners[i], Time.Now());
        }

        // 3 件目を Add した時点で 1 件目が evict される
        Action actEvicted = () => _ = owners[0].Buffer;
        actEvicted.Should().Throw<ObjectDisposedException>(
            "MaxSamples=2 で 3 件目を追加したら 1 件目の owner は evict されるべき");

        // 残りの 2 件は cache の using で解放される
    }

    [Fact]
    public void Dispose_は_全_owner_を_dispose_する()
    {
        using var cache = new WriterHistoryCache(TestGuid, maxSamples: 0);
        var owners = new RtpsPayloadOwner[3];
        for (int i = 0; i < 3; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64);
            owners[i] = new RtpsPayloadOwner(buffer);
            cache.Add(ChangeKind.Alive, buffer.AsMemory(0, 16), owners[i], Time.Now());
        }

        cache.Dispose();

        for (int i = 0; i < 3; i++)
        {
            int captured = i;
            Action act = () => _ = owners[captured].Buffer;
            act.Should().Throw<ObjectDisposedException>(
                $"owner[{captured}] が Dispose で解放されるべき");
        }
    }

    [Fact]
    public void Remove_は_該当_owner_を_dispose_する()
    {
        using var cache = new WriterHistoryCache(TestGuid);
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        cache.Add(ChangeKind.Alive, buffer.AsMemory(0, 16), owner, Time.Now());

        var removed = cache.Remove(new SequenceNumber(1L));

        removed.Should().BeTrue();
        Action act = () => _ = owner.Buffer;
        act.Should().Throw<ObjectDisposedException>(
            "Remove で owner が dispose されるべき");
    }
}
