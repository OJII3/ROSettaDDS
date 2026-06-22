# Publisher 所有権 payload + Batch API 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Publisher<T>.PublishAsync` 経路を zero-alloc 化し、`unity-to-ros2-reliable-32` の mps を 6 860 → 10 000+ (1.5x) にする。

**Architecture:** `CacheChange` が rented `byte[]` を `RtpsPayloadOwner` 経由で所有し、evict / ACK / writer Dispose 時に pool へ返す。`Publisher` に `PublishManyAsync(IReadOnlyList<T>)` / `PublishRepeatedAsync(T, int)` を追加し、batch 内の per-message `await` を `ConfigureAwait(false)` 経由で 1 回の main thread resume に集約。所有権の移動は `StatefulWriter.WriteOwnedAsync` / `WriteBatchAsync` 境界に集約し、Publisher 側の二重 dispose を防ぐ。

**Tech Stack:** .NET 8 / xunit / FluentAssertions / Unity 6000.3 (perf 計測のみ)

**参照 spec:** `docs/superpowers/specs/2026-06-22-publish-many-throughput-design.md`

---

## ファイル構成

### 新規
- `src/rosettadds/Rtps/HistoryCache/RtpsPayloadOwner.cs` — pool buffer を所有する軽量 IDisposable
- `tests/rosettadds.Tests/Rtps/HistoryCache/RtpsPayloadOwnerTests.cs` — RtpsPayloadOwner 単体テスト
- `tests/rosettadds.Tests/Rtps/HistoryCache/WriterHistoryCacheOwnedPayloadTests.cs` — owner lifetime テスト
- `tests/rosettadds.Tests/Integration/PublisherBatchTests.cs` — PublishManyAsync / PublishRepeatedAsync 統合テスト

### 変更
- `src/rosettadds/Rtps/HistoryCache/CacheChange.cs` — `PayloadOwner` プロパティ追加
- `src/rosettadds/Rtps/HistoryCache/WriterHistoryCache.cs` — `Add` overload + owner dispose + `Dispose` 新設
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` — `WriteOwnedAsync` / `WriteBatchAsync` 追加 + `Dispose` で history dispose
- `src/rosettadds/Dds/Publisher.cs` — `SerializeOwned` + `PublishAsync` 改修 + `PublishManyAsync` / `PublishRepeatedAsync` 追加
- `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` — publish loop を `PublishRepeatedAsync` に置換

### 既存 (回帰確認のみ)
- `tests/rosettadds.Tests/Integration/PublisherHotPathTests.cs` — allocation / throughput テスト
- `tests/rosettadds.Tests/Integration/PubSubLoopbackTests.cs`
- `tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs`
- `tests/rosettadds.Tests/Rtps/ParticipantRtpsReceiverTests.cs`

---

## Task 1: `RtpsPayloadOwner` クラス追加 (TDD)

**Files:**
- Create: `src/rosettadds/Rtps/HistoryCache/RtpsPayloadOwner.cs`
- Create: `tests/rosettadds.Tests/Rtps/HistoryCache/RtpsPayloadOwnerTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/rosettadds.Tests/Rtps/HistoryCache/RtpsPayloadOwnerTests.cs` を新規作成:

```csharp
using System.Buffers;
using ROSettaDDS.Rtps.HistoryCache;

namespace ROSettaDDS.Tests.Rtps.HistoryCache;

public class RtpsPayloadOwnerTests
{
    [Fact]
    public void Dispose_すると_rented_buffer_が_ArrayPool_に_戻される()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);

        owner.Dispose();

        // 同じサイズの buffer を rent すると、プールに返った buffer が
        // 再利用される可能性が高い (ArrayPool 実装依存だが、Rent 直後に
        // Dispose した buffer が含まれることを期待)
        var reused = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            // サイズが 64 以上であれば OK (ArrayPool は要求以上のサイズを返す)
            reused.Length.Should().BeGreaterThanOrEqualTo(64);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(reused);
        }
    }

    [Fact]
    public void Dispose_を_2回呼んでも_安全()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);

        owner.Dispose();
        owner.Dispose();  // 2 回目は no-op (例外を投げない)
    }
}
```

- [ ] **Step 2: テストが失敗することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~RtpsPayloadOwnerTests" 2>&1 | tail -20
```

期待される失敗: `RtpsPayloadOwner` 型が見つからないコンパイルエラー。

- [ ] **Step 3: 最小実装を追加**

`src/rosettadds/Rtps/HistoryCache/RtpsPayloadOwner.cs` を新規作成:

```csharp
using System.Buffers;
using System.Threading;

namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// 1 つの ArrayPool rent した byte[] を所有する軽量 IDisposable。
/// Dispose で ArrayPool に返す。多重 dispose は安全 (Interlocked.Exchange)。
/// </summary>
internal sealed class RtpsPayloadOwner : IDisposable
{
    private byte[]? _buffer;

    internal RtpsPayloadOwner(byte[] buffer)
    {
        _buffer = buffer;
    }

    /// <summary>所有している byte[] を取得。Dispose 後は例外。</summary>
    internal byte[] Buffer
    {
        get
        {
            var b = _buffer;
            if (b is null)
            {
                throw new ObjectDisposedException(nameof(RtpsPayloadOwner));
            }
            return b;
        }
    }

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        if (b is not null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~RtpsPayloadOwnerTests" 2>&1 | tail -20
```

期待: 2 tests passed。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Rtps/HistoryCache/RtpsPayloadOwner.cs tests/rosettadds.Tests/Rtps/HistoryCache/RtpsPayloadOwnerTests.cs
git commit -m "feat(rtps): RtpsPayloadOwner (ArrayPool-backed buffer owner) を追加"
```

---

## Task 2: `CacheChange.PayloadOwner` プロパティ追加

**Files:**
- Modify: `src/rosettadds/Rtps/HistoryCache/CacheChange.cs`

- [ ] **Step 1: `CacheChange` に `PayloadOwner` 内部プロパティと constructor 引数を追加**

`src/rosettadds/Rtps/HistoryCache/CacheChange.cs` を以下に置換:

```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// 1 サンプル (1 つの DATA submessage 相当) を表す不変オブジェクト。
/// RTPS 仕様 8.2.7 / 8.7.5 (CacheChange)。
/// </summary>
public sealed class CacheChange
{
    public ChangeKind Kind { get; }
    public Guid WriterGuid { get; }
    public SequenceNumber SequenceNumber { get; }
    public Time SourceTimestamp { get; }
    public ReadOnlyMemory<byte> SerializedPayload { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public CdrEndianness InlineQosEndianness { get; }
    internal RtpsPayloadOwner? PayloadOwner { get; }

    public CacheChange(
        ChangeKind kind,
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        Time sourceTimestamp,
        ReadOnlyMemory<byte> serializedPayload,
        ReadOnlyMemory<byte> inlineQos = default,
        CdrEndianness inlineQosEndianness = CdrEndianness.LittleEndian,
        RtpsPayloadOwner? payloadOwner = null)
    {
        Kind = kind;
        WriterGuid = writerGuid;
        SequenceNumber = sequenceNumber;
        SourceTimestamp = sourceTimestamp;
        SerializedPayload = serializedPayload;
        InlineQos = inlineQos;
        InlineQosEndianness = inlineQosEndianness;
        PayloadOwner = payloadOwner;
    }

    public override string ToString()
        => $"CacheChange({Kind}, writer={WriterGuid}, sn={SequenceNumber}, payload={SerializedPayload.Length}B)";
}
```

- [ ] **Step 2: 既存テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet build rosettadds.sln 2>&1 | tail -10
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj 2>&1 | tail -10
```

期待: 全テスト pass。`PayloadOwner` は optional なので既存呼び出しは無変更で動作。

- [ ] **Step 3: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Rtps/HistoryCache/CacheChange.cs
git commit -m "feat(rtps): CacheChange に PayloadOwner プロパティを追加"
```

---

## Task 3: `WriterHistoryCache` の `Add` overload + owner dispose + `Dispose` (TDD)

**Files:**
- Modify: `src/rosettadds/Rtps/HistoryCache/WriterHistoryCache.cs`
- Create: `tests/rosettadds.Tests/Rtps/HistoryCache/WriterHistoryCacheOwnedPayloadTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/rosettadds.Tests/Rtps/HistoryCache/WriterHistoryCacheOwnedPayloadTests.cs` を新規作成:

```csharp
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
        var cache = new WriterHistoryCache(TestGuid);
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        var payload = buffer.AsMemory(0, 16);

        var change = cache.Add(ChangeKind.Alive, payload, owner, Time.Now());

        change.PayloadOwner.Should().BeSameAs(owner,
            "CacheChange に渡した owner がそのまま保持されるべき");
    }

    [Fact]
    public void RemoveBelowOrEqual_は_該当_owner_を_dispose_する()
    {
        var cache = new WriterHistoryCache(TestGuid);
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
        var cache = new WriterHistoryCache(TestGuid, maxSamples: 2);
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

        // 残りの 2 件は生きている
        Action actKept1 = () => _ = owners[1].Buffer;
        Action actKept2 = () => _ = owners[2].Buffer;
        actKept1.Should().NotThrow();
        actKept2.Should().NotThrow();
    }

    [Fact]
    public void Dispose_は_全_owner_を_dispose_する()
    {
        var cache = new WriterHistoryCache(TestGuid, maxSamples: 0);
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
}
```

- [ ] **Step 2: テストが失敗することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~WriterHistoryCacheOwnedPayloadTests" 2>&1 | tail -20
```

期待: `Add` に 4 引数 overload が存在しない、または `PayloadOwner` プロパティがないためコンパイルエラー。

- [ ] **Step 3: 実装**

`src/rosettadds/Rtps/HistoryCache/WriterHistoryCache.cs` を以下に置換:

```csharp
using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// Writer 側の履歴キャッシュ。SequenceNumber を 1 から自動採番し、
/// (Reliable で) Reader からの再送要求に応えるためにサンプルを保持する。
/// KEEP_ALL 相当 (明示削除のみ)。<see cref="MaxSamples"/> を超えると古い順に自動削除される。
/// </summary>
public sealed class WriterHistoryCache : IDisposable
{
    private readonly object _lock = new();
    private readonly SortedDictionary<long, CacheChange> _changes = new();
    private long _lastSequence;
    private readonly Guid _writerGuid;
    private bool _disposed;

    /// <summary>保持できる最大サンプル数。0 以下なら無制限。</summary>
    public int MaxSamples { get; }
    public Guid WriterGuid => _writerGuid;

    public WriterHistoryCache(Guid writerGuid, int maxSamples = 0)
    {
        _writerGuid = writerGuid;
        MaxSamples = maxSamples;
        _lastSequence = 0;
    }

    /// <summary>新規サンプルを追加し、採番した <see cref="CacheChange"/> を返す (owner なし)。</summary>
    public CacheChange Add(ChangeKind kind, ReadOnlyMemory<byte> payload, Time sourceTimestamp)
        => Add(kind, payload, payloadOwner: null, sourceTimestamp);

    /// <summary>
    /// 新規サンプルを追加し、採番した <see cref="CacheChange"/> を返す。
    /// <paramref name="payloadOwner"/> が非 null の場合、history が所有権を持ち
    /// evict / ACK / Dispose 時に owner を release する。
    /// </summary>
    public CacheChange Add(
        ChangeKind kind,
        ReadOnlyMemory<byte> payload,
        RtpsPayloadOwner? payloadOwner,
        Time sourceTimestamp)
    {
        lock (_lock)
        {
            _lastSequence++;
            var sn = new SequenceNumber(_lastSequence);
            var change = new CacheChange(
                kind, _writerGuid, sn, sourceTimestamp, payload,
                payloadOwner: payloadOwner);
            _changes[_lastSequence] = change;
            EvictIfNeeded();
            return change;
        }
    }

    /// <summary>指定 SN のサンプルを取得する (再送用)。なければ null。</summary>
    public CacheChange? Get(SequenceNumber sn)
    {
        lock (_lock)
        {
            return _changes.TryGetValue(sn.Value, out var change) ? change : null;
        }
    }

    /// <summary>現在保持している最小 SN。空なら 0。</summary>
    public SequenceNumber FirstSequenceNumber
    {
        get
        {
            lock (_lock)
            {
                return _changes.Count == 0
                    ? new SequenceNumber(0L)
                    : new SequenceNumber(_changes.Keys.First());
            }
        }
    }

    /// <summary>これまでに採番した最大 SN (= 累積発行数)。</summary>
    public SequenceNumber LastSequenceNumber
    {
        get { lock (_lock) { return new SequenceNumber(_lastSequence); } }
    }

    /// <summary>現在保持しているサンプル数。</summary>
    public int Count
    {
        get { lock (_lock) { return _changes.Count; } }
    }

    /// <summary>指定範囲 [min, max] (両端含む) のサンプルを SN 順に列挙。</summary>
    public IReadOnlyList<CacheChange> EnumerateRange(SequenceNumber min, SequenceNumber max)
    {
        lock (_lock)
        {
            var result = new List<CacheChange>();
            foreach (var (key, change) in _changes)
            {
                if (key < min.Value) continue;
                if (key > max.Value) break;
                result.Add(change);
            }
            return result;
        }
    }

    /// <summary>指定 SN 以下のサンプルを破棄する (acked された分の解放)。owner も release する。</summary>
    public void RemoveBelowOrEqual(SequenceNumber sn)
    {
        lock (_lock)
        {
            var keysToRemove = _changes.Keys.Where(k => k <= sn.Value).ToArray();
            foreach (var k in keysToRemove)
            {
                if (_changes.TryGetValue(k, out var change))
                {
                    change.PayloadOwner?.Dispose();
                }
                _changes.Remove(k);
            }
        }
    }

    /// <summary>指定 SN のサンプルを破棄する。存在して削除できた場合は true。owner も release する。</summary>
    public bool Remove(SequenceNumber sn)
    {
        lock (_lock)
        {
            if (_changes.TryGetValue(sn.Value, out var change))
            {
                change.PayloadOwner?.Dispose();
                _changes.Remove(sn.Value);
                return true;
            }
            return false;
        }
    }

    private void EvictIfNeeded()
    {
        if (MaxSamples <= 0) return;
        while (_changes.Count > MaxSamples)
        {
            var firstKey = _changes.Keys.First();
            if (_changes.TryGetValue(firstKey, out var change))
            {
                change.PayloadOwner?.Dispose();
            }
            _changes.Remove(firstKey);
        }
    }

    /// <summary>全サンプルを破棄し、保持している全 owner を release する。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var change in _changes.Values)
            {
                change.PayloadOwner?.Dispose();
            }
            _changes.Clear();
        }
    }
}
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~WriterHistoryCacheOwnedPayloadTests" 2>&1 | tail -20
```

期待: 4 tests passed。

- [ ] **Step 5: 既存テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj 2>&1 | tail -10
```

期待: 全テスト pass。

- [ ] **Step 6: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Rtps/HistoryCache/WriterHistoryCache.cs tests/rosettadds.Tests/Rtps/HistoryCache/WriterHistoryCacheOwnedPayloadTests.cs
git commit -m "feat(rtps): WriterHistoryCache に owner-aware Add overload と Dispose を追加"
```

---

## Task 4: `StatefulWriter.WriteOwnedAsync` + `WriteBatchAsync` + `Dispose` (TDD)

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`

注: `WriteBatchAsync` の挙動は統合テスト (`PublisherBatchTests`, Task 6) で検証する。Task 4 では StateFulWriter の単体変更のみ。

- [ ] **Step 1: 既存 `StatefulWriter` 構造を確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
grep -n "WriteAsync\|public void Dispose\|_history\." src/rosettadds/Rtps/Writer/StatefulWriter.cs | head -20
```

期待: `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)` (line 191, 194) と `_history.Add(kind, payload, ts)` (line 200, 224) と `public void Dispose()` (line 283) の位置を確認。

- [ ] **Step 2: `WriteOwnedAsync` / `WriteBatchAsync` 追加 + `Dispose` で history dispose**

`src/rosettadds/Rtps/Writer/StatefulWriter.cs` の 3 箇所を修正する。

**修正 1**: クラス冒頭に `using` 追加 (既にあるか確認):

ファイルの先頭 (`using System.Buffers;` などの付近) を確認。`System.Buffers` がなければ追加:

```csharp
using System.Buffers;
```

**修正 2**: `WriteAsync(ReadOnlyMemory<byte>, ChangeKind, CancellationToken)` (line 194 付近) の **直後** に新メソッドを追加:

```csharp
        /// <summary>
        /// Pool-owned payload を 1 件送信。所有権は history に移転し、
        /// history.Add 失敗時のみ owner を release する。
        /// </summary>
        internal async ValueTask WriteOwnedAsync(
            RtpsPayloadOwner owner,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CacheChange change;
            try
            {
                change = _history.Add(ChangeKind.Alive, payload, owner, Time.Now());
            }
            catch
            {
                // history.Add 失敗時: owner は publisher 責任のまま。
                // ここで dispose しないと buffer がリークする。
                owner.Dispose();
                throw;
            }
            // Add 成功後: owner は history 所有。
            // SendDataAsync 失敗時は history evict / writer Dispose で release される。
            await SendDataAsync(change, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Pool-owned payload を N 件 batch 送信。N 件の Add を一括で行い、
        /// N 件の SendDataAsync を ConfigureAwait(false) 経由で await する。
        /// main thread は batch 終了時の 1 回の resume のみ。
        /// 所有権は history に移転。Add 失敗時のみ該当 owner を release。
        /// </summary>
        internal async ValueTask WriteBatchAsync(
            RtpsPayloadOwner[] owners,
            ReadOnlyMemory<byte>[] memories,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            int n = owners.Length;
            if (n != memories.Length)
            {
                throw new ArgumentException(
                    $"owners.Length ({n}) != memories.Length ({memories.Length})",
                    nameof(memories));
            }

            // 1. 全て history.Add (sync)。lock 競合は最小 (内部 lock のみ)。
            var changes = new CacheChange[n];
            for (int i = 0; i < n; i++)
            {
                try
                {
                    changes[i] = _history.Add(ChangeKind.Alive, memories[i], owners[i], Time.Now());
                }
                catch
                {
                    // 該当 owner のみ release。既に Add 済の分は history 所有。
                    owners[i].Dispose();
                    throw;
                }
            }

            // 2. 全 SendDataAsync を fire。ConfigureAwait(false) で continuation は ThreadPool。
            //    i=0 から順に追加された順に send (FIFO)。
            for (int i = 0; i < n; i++)
            {
                await SendDataAsync(changes[i], cancellationToken).ConfigureAwait(false);
            }
        }
```

**修正 3**: `public void Dispose()` (line 283 付近) を以下に置換:

```csharp
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _history.Dispose();
        }
```

- [ ] **Step 3: ビルドが通ることを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet build src/rosettadds/rosettadds.csproj 2>&1 | tail -10
```

期待: ビルド成功。警告は無視可。

- [ ] **Step 4: 既存テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests|FullyQualifiedName~PubSubLoopbackTests|FullyQualifiedName~ParticipantRtpsReceiverTests" 2>&1 | tail -10
```

期待: 全テスト pass (新規メソッド追加のみで既存 `WriteAsync` は無変更)。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "feat(rtps): StatefulWriter に WriteOwnedAsync / WriteBatchAsync を追加"
```

---

## Task 5: `Publisher.SerializeOwned` + `PublishAsync` の所有権化

**Files:**
- Modify: `src/rosettadds/Dds/Publisher.cs`

- [ ] **Step 1: `Publisher.cs` 冒頭に `using` 追加**

`using ROSettaDDS.Cdr;` などの付近に以下があるか確認。なければ追加:

```csharp
using System.Buffers;
using ROSettaDDS.Rtps.HistoryCache;
```

- [ ] **Step 2: `SerializeOwned` private ヘルパ追加 + `PublishAsync` を owned 化**

`src/rosettadds/Dds/Publisher.cs` の `PublishAsync` (line 40-45) を以下に置換:

```csharp
    /// <summary>値をシリアライズ (encap header CDR_LE 付き) して 1 件送信する。</summary>
    public async ValueTask PublishAsync(T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var (owner, memory) = SerializeOwned(value);
        // WriteOwnedAsync 内で Add 失敗時のみ owner は release 済み。
        // ここでは catch しない (二重 dispose / Use-After-Return 防止)。
        await _writer.WriteOwnedAsync(owner, memory, cancellationToken).ConfigureAwait(false);
    }
```

`SerializeWithEncapsulation` (line 62-72) の **直後** に以下を追加:

```csharp
    /// <summary>
    /// ArrayPool から rent した buffer に serialize し、所有者情報 (RtpsPayloadOwner) と
    /// ペイロード (ReadOnlyMemory) を返す。所有者は history に移転し、
    /// history.Add 失敗時のみ呼び出し側で release する。
    /// </summary>
    private (RtpsPayloadOwner owner, ReadOnlyMemory<byte> memory) SerializeOwned(T value)
    {
        int sizeEstimate = _serializer.GetSerializedSize(value);
        int totalCapacity = CdrEncapsulation.Size + sizeEstimate + 16;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalCapacity);
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        _serializer.Serialize(ref w, in value);
        int payloadLength = w.Position;
        var owner = new RtpsPayloadOwner(buffer);
        return (owner, buffer.AsMemory(0, payloadLength));
    }
```

- [ ] **Step 3: 既存 allocation テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PublisherHotPathTests" 2>&1 | tail -20
```

期待: `Publish_1件あたりの_GC_allocation_が_過剰でない` が pass (per-publish allocation が 2 KB 未満)。allocation 値は baseline (Task 5 変更前) より減少しているはず。

- [ ] **Step 4: 全テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj 2>&1 | tail -10
```

期待: 全テスト pass。

- [ ] **Step 5: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Dds/Publisher.cs
git commit -m "feat(dds): Publisher.PublishAsync を ArrayPool-backed owned payload 経由に"
```

---

## Task 6: `Publisher.PublishManyAsync` / `PublishRepeatedAsync` 追加 (TDD)

**Files:**
- Modify: `src/rosettadds/Dds/Publisher.cs`
- Create: `tests/rosettadds.Tests/Integration/PublisherBatchTests.cs`

- [ ] **Step 1: 失敗する統合テストを書く**

`tests/rosettadds.Tests/Integration/PublisherBatchTests.cs` を新規作成:

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherBatchTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
    }

    private static TestEnv CreatePair(TimeSpan? writerHeartbeatPeriod = null)
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

        var hbPeriod = writerHeartbeatPeriod ?? TimeSpan.FromSeconds(1);
        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            UserWriterHeartbeatPeriod = hbPeriod,
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
            UserWriterHeartbeatPeriod = hbPeriod,
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
    public async Task PublishManyAsync_は_全メッセージを_順序通り_受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "batch_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                if (int.TryParse(msg.Data, out var v))
                {
                    lock (lockObj) { received.Add(v); }
                }
            },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "batch_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 1000;
        var messages = new StringMessage[N];
        for (int i = 0; i < N; i++) messages[i] = new StringMessage(i.ToString());

        await pub.PublishManyAsync(messages);

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj) { if (received.Count == N) break; }
            await Task.Delay(10);
        }
        lock (lockObj)
        {
            received.Should().HaveCount(N);
            received.Should().Equal(Enumerable.Range(0, N));
        }
    }

    [Fact]
    public async Task PublishRepeatedAsync_は_同じ値を_count_回_publish_できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        int received = 0;
        using var sub = pB.CreateSubscription<StringMessage>(
            "repeat_topic",
            StringMessageSerializer.Instance,
            (_, _) => Interlocked.Increment(ref received),
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "repeat_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 500;
        var msg = new StringMessage("payload");
        await pub.PublishRepeatedAsync(msg, N);

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (Volatile.Read(ref received) < N && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Volatile.Read(ref received).Should().Be(N);
    }

    [Fact]
    public async Task PublishManyAsync_は_count_0_で_何もしない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;

        using var pub = pA.CreatePublisher<StringMessage>(
            "empty_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        // Start / WaitForMatched 不要。count=0 は即 return。
        await pub.PublishManyAsync(Array.Empty<StringMessage>());
    }

    [Fact]
    public async Task PublishRepeatedAsync_は_count_0_で_何もしない()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;

        using var pub = pA.CreatePublisher<StringMessage>(
            "empty_repeat_topic",
            StringMessageSerializer.Instance,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile);

        await pub.PublishRepeatedAsync(new StringMessage("x"), 0);
    }
}
```

- [ ] **Step 2: テストが失敗することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PublisherBatchTests" 2>&1 | tail -20
```

期待: `PublishManyAsync` / `PublishRepeatedAsync` メソッドが見つからないコンパイルエラー。

- [ ] **Step 3: 実装**

`src/rosettadds/Dds/Publisher.cs` の `PublishReturningSequenceNumberAsync` (line 47-59) の **直後** に以下を追加:

```csharp
    /// <summary>
    /// 複数の値を一括送信する batch API。所有 payload 経由で per-publish allocation を 0 にする。
    /// batch 内の per-message await は ConfigureAwait(false) 経由で 1 回の resume に集約。
    /// </summary>
    public async ValueTask PublishManyAsync(IReadOnlyList<T> values, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        int n = values.Count;
        var owners = new RtpsPayloadOwner[n];
        var memories = new ReadOnlyMemory<byte>[n];
        for (int i = 0; i < n; i++)
        {
            (owners[i], memories[i]) = SerializeOwned(values[i]);
        }
        // WriteBatchAsync 内で全 owner の所有権が history に移転。
        // 失敗時の release も WriteBatchAsync 側で完結。
        await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同じ値を <paramref name="count"/> 回連続送信する shortcut。
    /// 同一 value を使う perf harness / hot loop 向け。
    /// </summary>
    public ValueTask PublishRepeatedAsync(T value, int count, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (count <= 0) return default;

        // List<T> 確保を避けて直接 owner 配列で batch する内部 helper
        return PublishRepeatedCoreAsync(value, count, cancellationToken);
    }

    private async ValueTask PublishRepeatedCoreAsync(T value, int count, CancellationToken cancellationToken)
    {
        var owners = new RtpsPayloadOwner[count];
        var memories = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++)
        {
            (owners[i], memories[i]) = SerializeOwned(value);
        }
        await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: テストが成功することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PublisherBatchTests" 2>&1 | tail -20
```

期待: 4 tests passed。

- [ ] **Step 5: 全テストが pass することを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj 2>&1 | tail -10
```

期待: 全テスト pass。

- [ ] **Step 6: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add src/rosettadds/Dds/Publisher.cs tests/rosettadds.Tests/Integration/PublisherBatchTests.cs
git commit -m "feat(dds): Publisher に PublishManyAsync / PublishRepeatedAsync を追加"
```

---

## Task 7: `PerfPlayerEntry` の publish loop を `PublishRepeatedAsync` に置換

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs`

- [ ] **Step 1: 既存 loop を確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
grep -n "for (int i = 0; i < args.Messages" Ros2Unity/Assets/Perf/PerfPlayerEntry.cs
```

期待: `for (int i = 0; i < args.Messages; i++)` の位置を確認 (line 104-107 付近)。

- [ ] **Step 2: loop を `PublishRepeatedAsync` に置換**

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` の line 104-107 を以下に置換:

```csharp
                for (int i = 0; i < args.Messages; i++)
                {
                    await publisher.PublishAsync(message);
                }
```

を以下に置換:

```csharp
                await publisher.PublishRepeatedAsync(message, args.Messages);
```

- [ ] **Step 3: コミット (Unity Editor でのビルド検証は Task 8 でまとめて実施)**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add Ros2Unity/Assets/Perf/PerfPlayerEntry.cs
git commit -m "perf(unity): RunUnityToRos2 publish loop を PublishRepeatedAsync に置換"
```

---

## Task 8: Unity ビルド + perf runner で目標達成を検証

**Files:**
- (新規生成物のみ)
- `Ros2Unity/artifacts/perf/test-build/` (再ビルド)
- `artifacts/perf/<新ラン ID>/` (runner 出力)

- [ ] **Step 1: Unity Player を uloop で再ビルド**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
rm -rf Ros2Unity/artifacts/perf/test-build Ros2Unity/artifacts/perf/test-build_Data
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/test-build", "StandaloneLinux64", "mono"); return "ok";' 2>&1 | tail -5
```

期待: 5-10 分で完了、`Execution completed successfully`。

- [ ] **Step 2: ビルドが生成されたことを確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
ls Ros2Unity/artifacts/perf/test-build_Data 2>&1 | head -5
file Ros2Unity/artifacts/perf/test-build 2>&1
```

期待: `Managed/`, `MonoBleedingEdge/`, `Plugins/` 等が存在。`file` 出力は "ELF 64-bit LSB executable"。

- [ ] **Step 3: perf runner を実行**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
RID=$(date '+%Y%m%d-%H%M%S')
echo "Run ID: $RID" | tee artifacts/perf/runner-logs/runner-${RID}.log
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build Ros2Unity/artifacts/perf/test-build --scenario all --capture-frames 1200 2>&1 \
  | tee -a artifacts/perf/runner-logs/runner-${RID}.log | tail -30
```

期待: 5-10 分で完了、`Artifacts: artifacts/perf/<新 RID>` が出力。9 シナリオ全実行。

- [ ] **Step 4: 主要シナリオの mps を確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
RID=$(ls -t artifacts/perf/ | grep -E "^[0-9]{8}-[0-9]{6}$" | head -1)
echo "Latest run: $RID"
grep '"event":"measure_done"' artifacts/perf/$RID/unity-to-ros2-reliable-32/metrics.ndjson | python3 -c "
import json, sys
for line in sys.stdin:
    d = json.loads(line)
    print(f'unity-to-ros2-reliable-32: mps={d[\"messages_per_second\"]:.0f}, elapsed_ms={d[\"elapsed_ms\"]:.2f}, gc_alloc_total={d.get(\"gc_allocated_in_frame_bytes_total\", 0)/1024:.1f} KB')
"
```

期待される結果 (1.5x 改善目標):
- `mps >= 10,000` (baseline 6,860, 目標 10,000+)
- `elapsed_ms` が baseline (72.88 ms) から短縮
- `gc_alloc_total` が baseline (~4.4 MB) から大幅減

- [ ] **Step 5: ベースライン (20260622-092922) と新ランの比較表を生成**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
NEW_RID=$(ls -t artifacts/perf/ | grep -E "^[0-9]{8}-[0-9]{6}$" | head -1)
echo "Baseline (20260622-092922) vs New ($NEW_RID):"
for s in unity-to-ros2-reliable-32 unity-to-ros2-reliable-1024 ros2-to-unity-reliable-32 ros2-to-unity-reliable-1024; do
  base=$(grep '"event":"measure_done"' artifacts/perf/20260622-092922/$s/metrics.ndjson 2>/dev/null | python3 -c "import json,sys; d=json.loads(sys.stdin.read()); print(f'{d[\"messages_per_second\"]:.0f}')" 2>/dev/null || echo "N/A")
  new=$(grep '"event":"measure_done"' artifacts/perf/$NEW_RID/$s/metrics.ndjson 2>/dev/null | python3 -c "import json,sys; d=json.loads(sys.stdin.read()); print(f'{d[\"messages_per_second\"]:.0f}')" 2>/dev/null || echo "N/A")
  echo "  $s: base=$base mps, new=$new mps"
done
```

期待: 主要 4 シナリオで baseline 比 +20% 以上 (allocation 削減 + batch 効果)。

- [ ] **Step 6: 成功基準の判定**

以下をすべて満たすこと:

| 基準 | 期待値 | 実測 | 判定 |
|---|---|---|---|
| unity-to-ros2-reliable-32 mps | ≥ 10,000 | (Step 4) | ○ / × |
| gc_allocated_in_frame_bytes_total (reliable-32) | baseline の 50% 以下 | (Step 4) | ○ / × |
| 既存 9 シナリオで timeout / error 退行なし | なし | (Step 3) | ○ / × |
| `dotnet test` 全 pass | pass | (Task 5/6 で確認済) | ○ / × |

1 項目でも × の場合は spec を再評価。

- [ ] **Step 7: 結果に応じて追加コミット / ロールバック判定**

- 全 ○: Task 9 へ進む (検証レポート更新 + PR 準備)
- 1 項目でも ×:
  - allocation が減っていない → `SerializeOwned` が `_serializer.GetSerializedSize` で過小評価していないか確認
  - mps 改善不足 → `_writer.WriteBatchAsync` 内の sequential await を `Task.WhenAll` に置換 (将来の改善余地)
  - 既存テスト fail → 該当 commit を `git revert` して再設計

- [ ] **Step 8: 検証レポート spec を更新**

`docs/superpowers/specs/2026-06-22-perf-followup-verification.md` の「アクション 1 未達」セクションを、Task 8 の実測結果で更新:

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
# 該当セクションを編集 (具体的な mps 値と改善率に置換)
```

- [ ] **Step 9: コミット**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git add docs/superpowers/specs/2026-06-22-perf-followup-verification.md
git commit -m "docs: perf followup 検証レポートをアクション 1 改善後の実測で更新"
```

---

## Task 9: PR 準備

- [ ] **Step 1: 全変更履歴を確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git log --oneline main..HEAD
git status
```

期待: Task 1〜9 のコミットが並んでいる。`git status` は clean。

- [ ] **Step 2: ビルド + 全テスト最終確認**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
dotnet build rosettadds.sln 2>&1 | tail -5
dotnet test rosettadds.sln 2>&1 | tail -10
```

期待: ビルド成功、全テスト pass。

- [ ] **Step 3: ブランチを push**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
git push -u origin feat/publisher-owned-payload
```

- [ ] **Step 4: PR 作成**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS
gh pr create --base main --title "perf: Publisher 所有権 payload + batch API で Unity→ROS 2 mps 1.5x 改善" --body "$(cat <<'EOF'
## 概要

`docs/superpowers/specs/2026-06-22-publisher-hot-path-arraypool.md` (#86) の続編として、
`Publisher<T>.PublishAsync` 経路の payload allocation を削減し batch API を追加。
`unity-to-ros2-reliable-32` の mps を 6,860 → 10,000+ (1.5x) に改善。

## 変更点

- `RtpsPayloadOwner` (新規): ArrayPool-rented buffer を所有する軽量 IDisposable
- `CacheChange.PayloadOwner` (新規プロパティ): history が所有権を持つ
- `WriterHistoryCache.Add` の owner-aware overload + evict / ACK / Dispose で owner release
- `StatefulWriter.WriteOwnedAsync` / `WriteBatchAsync` (新規 internal API)
- `Publisher.PublishAsync` 経路の owned payload 化
- `Publisher.PublishManyAsync(IReadOnlyList<T>)` / `PublishRepeatedAsync(T, int)` (新規 public API)
- `PerfPlayerEntry` の publish loop を `PublishRepeatedAsync` に置換

## 検証

- ユニットテスト: `RtpsPayloadOwnerTests`, `WriterHistoryCacheOwnedPayloadTests`
- 統合テスト: `PublisherBatchTests` (新規 4 件)
- 既存テスト: 全 pass
- allocation: 1 publish あたり 24 MB/sec → 大幅減 (Task 8 で実測)
- throughput: `unity-to-ros2-reliable-32` 6,860 → 10,000+ mps (Task 8 で実測)

## 参照

- spec: `docs/superpowers/specs/2026-06-22-publish-many-throughput-design.md`
- plan: `docs/superpowers/plans/2026-06-22-publish-many-throughput.md`
- 親 spec: `docs/superpowers/specs/2026-06-22-publisher-hot-path-arraypool.md`
- 検証レポート: `docs/superpowers/specs/2026-06-22-perf-followup-verification.md`
EOF
)"
```

期待: PR URL が出力される。`main` への merge は別途ユーザー判断。

---

## メモ

- **TDD 規約**: 全タスクでテスト先行。テスト失敗 → 実装 → テスト成功 → コミット
- **段階的コミット**: 各タスクは独立したコミット。fail 時はそのタスクだけ `git revert` できる粒度
- **TDD 順序**: `RtpsPayloadOwner` → `CacheChange` → `WriterHistoryCache` → `StatefulWriter` → `Publisher` の依存順
- **エラー処理**: 所有権移動は `StatefulWriter` 境界に集約。Publisher は catch しない
- **既存 API 互換**: `Publisher.SerializeWithEncapsulation(T)` は無変更。`Publisher.PublishAsync` も無変更
- **計測バイアス回避**: perf runner の `measure_start` は match 後。warmup 影響なし
