# StatefulWriter 責務分割 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** StatefulWriterの密結合を解消し、公開API・wire出力を完全に維持したまま3つの内部クラスへ段階的に責務を分割する。

**Architecture:** StatefulWriterをファサードとして残し、Reader管理、packet送信、background task管理のみを内部クラスへ抽出する。各抽出前にcharacterization testで既存挙動を固定する。

**Tech Stack:** C#/.NET 8, xUnit, FluentAssertions, ROSettaDDS RTPS

---

## Task 1: 既存契約をテストで固定

**Files:**
- Create: `tests/rosettadds.Tests/Rtps/StatefulWriterMatchingTests.cs`
- Create: `tests/rosettadds.Tests/Rtps/StatefulWriterPacketContractTests.cs`
- Create: `tests/rosettadds.Tests/Rtps/BackgroundOperationTrackerTests.cs`

- [ ] **Step 1: MatchReader 契約テストを作成**

```csharp
// tests/rosettadds.Tests/Rtps/StatefulWriterMatchingTests.cs
using System.Diagnostics;
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class StatefulWriterMatchingTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    private sealed class Setup
    {
        public required LoopbackHub Hub { get; init; }
        public required LoopbackTransport WriterTransport { get; init; }
        public required LoopbackTransport ReaderTransport { get; init; }
        public required Locator WriterLocator { get; init; }
        public required Locator ReaderLocator { get; init; }
        public required GuidPrefix WriterPrefix { get; init; }
        public required GuidPrefix ReaderPrefix { get; init; }
        public required EntityId WriterEntityId { get; init; }
        public required EntityId ReaderEntityId { get; init; }
    }

    private static Setup CreateSetup()
    {
        var hub = new LoopbackHub();
        var writerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var readerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);
        var writerTr = hub.Create(writerLoc);
        var readerTr = hub.Create(readerLoc);
        return new Setup
        {
            Hub = hub,
            WriterTransport = writerTr,
            ReaderTransport = readerTr,
            WriterLocator = writerLoc,
            ReaderLocator = readerLoc,
            WriterPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x01),
            ReaderPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x02),
            WriterEntityId = new EntityId(0x0000_0001u, EntityKind.UserDefinedWriterNoKey),
            ReaderEntityId = new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey),
        };
    }

    private static StatefulWriter CreateWriter(Setup s, out WriterHistoryCache history)
    {
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        history = new WriterHistoryCache(writerGuid);
        return new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history);
    }

    [Fact]
    public void duplicate_matchは累積件数を増やさずLocatorだけ更新する()
    {
        var s = CreateSetup();
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        using var writer = CreateWriter(s, out _);
        var firstLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.20"), 8000u);
        var updatedLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.21"), 8001u);

        writer.MatchReader(readerGuid, firstLocator, ReliabilityKind.Reliable);
        writer.MatchReader(readerGuid, updatedLocator, ReliabilityKind.Reliable);

        writer.MatchedReaderCount.Should().Be(1);
        writer.GetReaderProxy(readerGuid)!.UnicastLocator.Should().Be(updatedLocator);
        var status = writer.PublicationMatchedStatus;
        status.CurrentCount.Should().Be(1);
        status.TotalCount.Should().Be(1);
    }

    [Fact]
    public void unmatch_and_rematchは現在件数と累積件数を正しく更新する()
    {
        var s = CreateSetup();
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        using var writer = CreateWriter(s, out _);

        writer.MatchReader(readerGuid, s.ReaderLocator);
        writer.MatchedReaderCount.Should().Be(1);
        var statusAfterMatch = writer.PublicationMatchedStatus;
        statusAfterMatch.CurrentCount.Should().Be(1);
        statusAfterMatch.TotalCount.Should().Be(1);

        writer.UnmatchReader(readerGuid);
        writer.MatchedReaderCount.Should().Be(0);
        var statusAfterUnmatch = writer.PublicationMatchedStatus;
        statusAfterUnmatch.CurrentCount.Should().Be(0);
        statusAfterUnmatch.TotalCount.Should().Be(1);

        writer.MatchReader(readerGuid, s.ReaderLocator);
        writer.MatchedReaderCount.Should().Be(1);
        var statusAfterRematch = writer.PublicationMatchedStatus;
        statusAfterRematch.CurrentCount.Should().Be(1);
        statusAfterRematch.TotalCount.Should().Be(2);
    }

    [Fact]
    public void PublicationMatchedStatus取得後にchange値がリセットされる()
    {
        var s = CreateSetup();
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        using var writer = CreateWriter(s, out _);

        writer.MatchReader(readerGuid, s.ReaderLocator);
        _ = writer.PublicationMatchedStatus; // consume initial
        writer.MatchReader(new Guid(s.ReaderPrefix, new EntityId(0x0000_0003u, EntityKind.UserDefinedReaderNoKey)), s.ReaderLocator);

        var status = writer.PublicationMatchedStatus;
        status.CurrentCountChange.Should().Be(1);
        status.TotalCountChange.Should().Be(1);

        var statusAgain = writer.PublicationMatchedStatus;
        statusAgain.CurrentCountChange.Should().Be(0);
        statusAgain.TotalCountChange.Should().Be(0);
    }

    [Fact]
    public void 複数reliable_readerの最小ACKまでpurgeする()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid1 = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var readerGuid2 = new Guid(GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x03), s.ReaderEntityId);
        using var writer = CreateWriter(s, out var history);

        writer.MatchReader(readerGuid1, s.ReaderLocator);
        writer.MatchReader(readerGuid2, s.ReaderLocator);

        // 5 samples
        for (int i = 1; i <= 5; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        // reader1 acks SN=3
        writer.GetReaderProxy(readerGuid1)!.ProcessAckNack(
            new SequenceNumberSet(new SequenceNumber(4L), 0, Array.Empty<uint>()));
        // reader2 acks SN=5
        writer.GetReaderProxy(readerGuid2)!.ProcessAckNack(
            new SequenceNumberSet(new SequenceNumber(6L), 0, Array.Empty<uint>()));

        // ProcessPacket triggers purge
        var ackPacket1 = BuildAckNackPacket(s.ReaderPrefix, s.ReaderEntityId, s.WriterEntityId,
            new SequenceNumberSet(new SequenceNumber(4L), 0, Array.Empty<uint>()));
        writer.ProcessPacket(ackPacket1);

        history.Count.Should().Be(2, "minimum acked is SN=3, so SN<=3 should be purged");
    }

    [Fact]
    public void best_effort_readerはpurge判定から除外する()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var beReaderGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        using var writer = CreateWriter(s, out var history);

        writer.MatchReader(beReaderGuid, s.ReaderLocator, ReliabilityKind.BestEffort);

        for (int i = 1; i <= 3; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        var ackPacket = BuildAckNackPacket(s.ReaderPrefix, s.ReaderEntityId, s.WriterEntityId,
            new SequenceNumberSet(new SequenceNumber(4L), 0, Array.Empty<uint>()));
        writer.ProcessPacket(ackPacket);

        history.Count.Should().Be(3, "best-effort reader should not trigger purge");
    }

    [Fact]
    public void reliable_reader不在時はpurgeしない()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out var history);

        for (int i = 1; i <= 3; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        // no readers matched
        history.Count.Should().Be(3);
    }

    private static byte[] BuildAckNackPacket(
        GuidPrefix readerPrefix, EntityId readerEntityId, EntityId writerEntityId,
        SequenceNumberSet snSet)
    {
        var buffer = new byte[1500];
        var w = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.ROSettaDDS, readerPrefix);
        w.WriteAckNack(new AckNackSubmessage(readerEntityId, writerEntityId, snSet, final: false));
        var packet = new byte[w.BytesWritten];
        w.WrittenSpan.CopyTo(packet);
        return packet;
    }
}
```

- [ ] **Step 2: テストを実行して失敗を確認**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulWriterMatchingTests" --no-restore 2>&1 | tail -5
```

- [ ] **Step 3: PacketContract テストを作成**

```csharp
// tests/rosettadds.Tests/Rtps/StatefulWriterPacketContractTests.cs
using System.Diagnostics;
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class StatefulWriterPacketContractTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    private sealed class Setup
    {
        public required LoopbackHub Hub { get; init; }
        public required LoopbackTransport WriterTransport { get; init; }
        public required LoopbackTransport ReaderTransport { get; init; }
        public required Locator WriterLocator { get; init; }
        public required Locator ReaderLocator { get; init; }
        public required GuidPrefix WriterPrefix { get; init; }
        public required GuidPrefix ReaderPrefix { get; init; }
        public required EntityId WriterEntityId { get; init; }
        public required EntityId ReaderEntityId { get; init; }
    }

    private static Setup CreateSetup()
    {
        var hub = new LoopbackHub();
        var writerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var readerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);
        var writerTr = hub.Create(writerLoc);
        var readerTr = hub.Create(readerLoc);
        return new Setup
        {
            Hub = hub,
            WriterTransport = writerTr,
            ReaderTransport = readerTr,
            WriterLocator = writerLoc,
            ReaderLocator = readerLoc,
            WriterPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x01),
            ReaderPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x02),
            WriterEntityId = new EntityId(0x0000_0001u, EntityKind.UserDefinedWriterNoKey),
            ReaderEntityId = new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey),
        };
    }

    private static StatefulWriter CreateWriter(Setup s, out WriterHistoryCache history)
    {
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        history = new WriterHistoryCache(writerGuid);
        return new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history);
    }

    [Fact]
    public async Task 空historyのHEARTBEATはfirstSN1_lastSN0()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        writer.MatchReader(readerGuid, s.ReaderLocator);

        var hbTcs = new TaskCompletionSource<HeartbeatSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Heartbeat)
                    hbTcs.TrySetResult(HeartbeatSubmessage.ReadBody(body, header.Endianness, header.Flags));
            }
        };

        writer.Start();
        var hb = await hbTcs.Task.WaitAsync(ReceiveTimeout);
        hb.FirstSequenceNumber.Value.Should().Be(1L);
        hb.LastSequenceNumber.Value.Should().Be(0L);
    }

    [Fact]
    public async Task Alive_DATAのpacket構築が不変()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        writer.MatchReader(readerGuid, s.ReaderLocator);

        var dataTcs = new TaskCompletionSource<DataSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Data)
                    dataTcs.TrySetResult(DataSubmessage.ReadBody(body, header.Endianness, header.Flags));
            }
        };

        await writer.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
        var data = await dataTcs.Task.WaitAsync(ReceiveTimeout);
        data.WriterSequenceNumber.Value.Should().Be(1L);
        data.SerializedPayload.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task pre_join_SNのNACKにGAPを返す()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out var history);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);

        for (int i = 1; i <= 3; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }
        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

        int gapCount = 0;
        var gapTcs = new TaskCompletionSource<GapSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Gap)
                {
                    Interlocked.Increment(ref gapCount);
                    gapTcs.TrySetResult(GapSubmessage.ReadBody(body, header.Endianness, header.Flags));
                }
            }
        };

        var ackPacket = BuildAckNackPacket(s.ReaderPrefix, s.ReaderEntityId, s.WriterEntityId,
            new SequenceNumberSet(new SequenceNumber(1L), 1, new[] { 0x80000000u }));
        writer.ProcessPacket(ackPacket);

        var gap = await gapTcs.Task.WaitAsync(ReceiveTimeout);
        gap.GapStart.Value.Should().Be(1L);
        gap.ReaderEntityId.Should().Be(s.ReaderEntityId);
        gap.WriterEntityId.Should().Be(s.WriterEntityId);
    }

    private static byte[] BuildAckNackPacket(
        GuidPrefix readerPrefix, EntityId readerEntityId, EntityId writerEntityId,
        SequenceNumberSet snSet)
    {
        var buffer = new byte[1500];
        var w = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.ROSettaDDS, readerPrefix);
        w.WriteAckNack(new AckNackSubmessage(readerEntityId, writerEntityId, snSet, final: false));
        var packet = new byte[w.BytesWritten];
        w.WrittenSpan.CopyTo(packet);
        return packet;
    }
}
```

- [ ] **Step 4: テストを実行して失敗を確認**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulWriterPacketContractTests" --no-restore 2>&1 | tail -5
```

- [ ] **Step 5: BackgroundOperationTracker テストを作成**

```csharp
// tests/rosettadds.Tests/Rtps/BackgroundOperationTrackerTests.cs
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Rtps;

public class BackgroundOperationTrackerTests
{
    [Fact]
    public void 正常完了したタスクは待機不要()
    {
        var tracker = new BackgroundOperationTracker();
        var completed = false;
        tracker.Run(async ct =>
        {
            await Task.Delay(10, ct);
            completed = true;
        }, "test", CancellationToken.None);

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
        completed.Should().BeTrue();
    }

    [Fact]
    public void キャンセル例外は警告なしで正常終了()
    {
        var tracker = new BackgroundOperationTracker();
        tracker.Run(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
        }, "test", new CancellationToken(true));

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void 通常例外はWarnでログされる()
    {
        var logger = new CollectingLogger();
        var tracker = new BackgroundOperationTracker(logger);
        tracker.Run(_ => throw new InvalidOperationException("boom"), "test", CancellationToken.None);

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
        logger.Warns.Should().Contain(m => m.Contains("test"));
    }

    [Fact]
    public void 複数タスクを並列実行して待機する()
    {
        var counter = 0;
        var tracker = new BackgroundOperationTracker();
        for (int i = 0; i < 5; i++)
        {
            tracker.Run(async ct =>
            {
                await Task.Delay(10, ct);
                Interlocked.Increment(ref counter);
            }, $"task{i}", CancellationToken.None);
        }

        tracker.WaitForCompletion(TimeSpan.FromSeconds(2));
        Volatile.Read(ref counter).Should().Be(5);
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warns { get; } = new();
        public bool IsEnabled(LogLevel level) => true;
        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            if (level == LogLevel.Warn) Warns.Add(message);
        }
    }
}
```

- [ ] **Step 6: テストを実行して失敗を確認**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~BackgroundOperationTrackerTests" --no-restore 2>&1 | tail -5
```

- [ ] **Step 7: コミット**

```bash
git add tests/rosettadds.Tests/Rtps/StatefulWriterMatchingTests.cs tests/rosettadds.Tests/Rtps/StatefulWriterPacketContractTests.cs tests/rosettadds.Tests/Rtps/BackgroundOperationTrackerTests.cs
git commit -m "test(rtps): StatefulWriterの既存契約をcharacterization testで固定する"
```

## Task 2: MatchedReaderRegistry を抽出

**Files:**
- Create: `src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs`
- Create: `src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs.meta`
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`

- [ ] **Step 1: MatchedReaderRegistry を作成**

```csharp
// src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Dds;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// StatefulWriter が保持する matched reader の集合と PublicationMatchedStatus を管理する。
/// </summary>
internal sealed class MatchedReaderRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ReaderProxy> _matched = new();
    private long _totalMatchedReaders;
    private Guid? _lastSubscriptionHandle;
    private int _lastReportedCurrentReaders;
    private long _lastReportedTotalReaders;

    /// <summary>
    /// remote reader を match する。既存の場合は locator を更新する。
    /// </summary>
    public (ReaderProxy Proxy, bool Added) Match(
        Guid readerGuid,
        Locator? locator,
        ReliabilityKind reliability)
    {
        lock (_lock)
        {
            if (_matched.TryGetValue(readerGuid, out var existing))
            {
                existing.UpdateUnicastLocator(locator);
                return (existing, false);
            }
            else
            {
                var proxy = new ReaderProxy(readerGuid, locator, reliability);
                _matched[readerGuid] = proxy;
                _totalMatchedReaders++;
                _lastSubscriptionHandle = readerGuid;
                return (proxy, true);
            }
        }
    }

    public void Unmatch(Guid readerGuid)
    {
        lock (_lock) { _matched.Remove(readerGuid); }
    }

    public ReaderProxy? Find(Guid readerGuid)
    {
        lock (_lock) { return _matched.TryGetValue(readerGuid, out var p) ? p : null; }
    }

    public ReaderProxy[] Snapshot()
    {
        lock (_lock) { return _matched.Values.ToArray(); }
    }

    public int Count
    {
        get { lock (_lock) { return _matched.Count; } }
    }

    public PublicationMatchedStatus TakePublicationMatchedStatus()
    {
        int current;
        long total;
        int currentChange;
        long totalChange;
        Guid? lastHandle;
        lock (_lock)
        {
            current = _matched.Count;
            total = _totalMatchedReaders;
            lastHandle = _lastSubscriptionHandle;
            currentChange = current - _lastReportedCurrentReaders;
            totalChange = total - _lastReportedTotalReaders;
            _lastReportedCurrentReaders = current;
            _lastReportedTotalReaders = total;
        }
        return new PublicationMatchedStatus
        {
            CurrentCount = current,
            CurrentCountChange = currentChange,
            TotalCount = checked((int)Math.Min(total, int.MaxValue)),
            TotalCountChange = checked((int)Math.Min(totalChange, int.MaxValue)),
            LastSubscriptionHandle = lastHandle,
        };
    }

    /// <summary>
    /// reliable reader のみを対象に、全 reader の最小 acked SN を返す。
    /// reliable reader が 1 つもない場合は null。
    /// </summary>
    public SequenceNumber? MinimumReliableAcknowledged()
    {
        lock (_lock)
        {
            if (_matched.Count == 0) return null;
            long minAcked = long.MaxValue;
            bool hasReliable = false;
            foreach (var proxy in _matched.Values)
            {
                if (!proxy.IsReliable) continue;
                hasReliable = true;
                var acked = proxy.HighestAcked.Value;
                if (acked < minAcked) minAcked = acked;
            }
            if (!hasReliable) return null;
            return new SequenceNumber(minAcked);
        }
    }
}
```

- [ ] **Step 2: StatefulWriter を修正して Registry に委譲**

StatefulWriter の `_matched`, `_totalMatchedReaders`, `_lastSubscriptionHandle`, `_lastReportedCurrentReaders`, `_lastReportedTotalReaders` フィールドと `_matchedLock` を削除し、`_registry` フィールドに置き換える。

```csharp
// StatefulWriter.cs の変更点
private readonly MatchedReaderRegistry _registry = new();
```

`MatchReader`, `UnmatchReader`, `GetReaderProxy`, `MatchedReaders`, `MatchedReaderCount`, `PublicationMatchedStatus`, `PurgeAckedSamples` を委譲に変更。

- [ ] **Step 3: テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulWriter" --no-restore 2>&1 | tail -5
```

- [ ] **Step 4: 全テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --no-restore 2>&1 | tail -10
```

- [ ] **Step 5: .meta ファイルを作成してコミット**

```bash
# Unity .meta ファイルを生成
# (Unity GUID はランダムに生成)
cat > src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs.meta << 'EOF'
fileFormatVersion: 2
guid: <random-guid>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF

git add src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs src/rosettadds/Rtps/Writer/MatchedReaderRegistry.cs.meta src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "refactor(rtps): StatefulWriterからreader管理をMatchedReaderRegistryに分離する"
```

## Task 3: StatefulWriterPacketSender を抽出

**Files:**
- Create: `src/rosettadds/Rtps/Writer/StatefulWriterPacketSender.cs`
- Create: `src/rosettadds/Rtps/Writer/StatefulWriterPacketSender.cs.meta`
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`

- [ ] **Step 1: StatefulWriterPacketSender を作成**

```csharp
// src/rosettadds/Rtps/Writer/StatefulWriterPacketSender.cs
using System.Buffers;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.Submessages;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// StatefulWriter の packet 構築と送信を担当する内部クラス。
/// </summary>
internal sealed class StatefulWriterPacketSender
{
    public const int SendBufferSize = 1500;
    public const int DataFragPayloadSize = 1024;

    private readonly IRtpsTransport _transport;
    private readonly Locator _multicastDestination;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly EntityId _writerEntityId;
    private readonly ILogger _logger;

    public StatefulWriterPacketSender(
        IRtpsTransport transport,
        Locator multicastDestination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId writerEntityId,
        ILogger logger)
    {
        _transport = transport;
        _multicastDestination = multicastDestination;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _writerEntityId = writerEntityId;
        _logger = logger;
    }

    public async ValueTask SendDataAsync(
        CacheChange change,
        EntityId readerEntityId,
        Locator destination,
        CancellationToken cancellationToken)
    {
        bool isAlive = change.Kind == ChangeKind.Alive;
        ReadOnlyMemory<byte> inlineQos = isAlive
            ? default
            : DataSubmessage.BuildStatusInfoInlineQos(ToStatusInfo(change.Kind), CdrEndianness.LittleEndian);

        int dataMessageSize = RtpsHeader.Size
            + SubmessageHeader.Size + Time.Size
            + SubmessageHeader.Size + DataSubmessage.FixedHeaderSize
            + inlineQos.Length
            + change.SerializedPayload.Length;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(SendBufferSize);
        try
        {
            if (dataMessageSize <= SendBufferSize)
            {
                BuildDataPacket(change, readerEntityId, inlineQos, isAlive, scratch, out int written);
                await _transport.SendAsync(scratch.AsMemory(0, written), destination, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await SendDataFragPacketsSequentialAsync(
                change, readerEntityId, inlineQos, isAlive, scratch, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("StatefulWriter DATA send failed", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public async ValueTask SendHeartbeatAsync(
        SequenceNumber first,
        SequenceNumber last,
        EntityId readerEntityId,
        Locator destination,
        int count,
        CancellationToken cancellationToken)
    {
        var hb = new HeartbeatSubmessage(
            readerEntityId, _writerEntityId, first, last, count, final: false, liveliness: false);

        var buffer = new byte[SendBufferSize];
        var msg = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        msg.WriteHeartbeat(hb);
        var packet = new byte[msg.BytesWritten];
        msg.WrittenSpan.CopyTo(packet);

        try
        {
            await _transport.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("StatefulWriter HEARTBEAT send failed", ex);
        }
    }

    public async ValueTask SendGapAsync(
        SequenceNumber missingSequenceNumber,
        EntityId readerEntityId,
        Locator destination,
        CancellationToken cancellationToken)
    {
        var gap = new GapSubmessage(
            readerEntityId: readerEntityId,
            writerEntityId: _writerEntityId,
            gapStart: missingSequenceNumber,
            gapList: new SequenceNumberSet(missingSequenceNumber + 1, 0, Array.Empty<uint>()));

        var buffer = new byte[SendBufferSize];
        var writer = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        writer.WriteGap(gap);
        var packet = new byte[writer.BytesWritten];
        writer.WrittenSpan.CopyTo(packet);

        try
        {
            await _transport.SendAsync(packet, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("StatefulWriter GAP send failed", ex);
        }
    }

    // ... (BuildDataPacket, SendDataFragPacketsSequentialAsync, WriteDataFragToScratch, ToStatusInfo)
}
```

- [ ] **Step 2: StatefulWriter を修正して Sender に委譲**

StatefulWriter の `BuildHeartbeatPacket`, `BuildGapPacket`, `BuildDataPacket`, `SendDataFragPacketsSequentialAsync`, `WriteDataFragToScratch`, `ToStatusInfo` を削除し、`_sender` メソッド呼び出しに置き換える。

- [ ] **Step 3: テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulWriter" --no-restore 2>&1 | tail -5
```

- [ ] **Step 4: 全テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --no-restore 2>&1 | tail -10
```

- [ ] **Step 5: .meta ファイルを作成してコミット**

```bash
git add src/rosettadds/Rtps/Writer/StatefulWriterPacketSender.cs src/rosettadds/Rtps/Writer/StatefulWriterPacketSender.cs.meta src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "refactor(rtps): StatefulWriterからpacket送信をStatefulWriterPacketSenderに分離する"
```

## Task 4: BackgroundOperationTracker を抽出

**Files:**
- Create: `src/rosettadds/Rtps/Writer/BackgroundOperationTracker.cs`
- Create: `src/rosettadds/Rtps/Writer/BackgroundOperationTracker.cs.meta`
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`

- [ ] **Step 1: BackgroundOperationTracker を作成**

```csharp
// src/rosettadds/Rtps/Writer/BackgroundOperationTracker.cs
using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// StatefulWriter の非同期タスク追跡と終了待機を担当する内部クラス。
/// </summary>
internal sealed class BackgroundOperationTracker
{
    private readonly object _lock = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly ILogger _logger;

    public BackgroundOperationTracker(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public void Run(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var task = RunAsync(operation, operationName, cancellationToken);
        lock (_lock)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_lock)
                {
                    _tasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void WaitForCompletion(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_lock)
        {
            tasks = _tasks.ToArray();
        }

        if (tasks.Length == 0) return;

        try
        {
            if (!Task.WaitAll(tasks, timeout))
            {
                _logger.Warn("StatefulWriter background tasks did not exit cleanly");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }
        catch (Exception ex)
        {
            _logger.Warn("StatefulWriter background tasks did not exit cleanly", ex);
        }
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"{operationName} failed", ex);
        }
    }
}
```

- [ ] **Step 2: StatefulWriter を修正して Tracker に委譲**

StatefulWriter の `_backgroundTasksLock`, `_backgroundTasks`, `RunBackground`, `RunBackgroundAsync`, `WaitForBackgroundTasks` を削除し、`_tracker` フィールドに置き換える。

- [ ] **Step 3: テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulWriter" --no-restore 2>&1 | tail -5
```

- [ ] **Step 4: 全テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --no-restore 2>&1 | tail -10
```

- [ ] **Step 5: .meta ファイルを作成してコミット**

```bash
git add src/rosettadds/Rtps/Writer/BackgroundOperationTracker.cs src/rosettadds/Rtps/Writer/BackgroundOperationTracker.cs.meta src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "refactor(rtps): StatefulWriterから非同期task管理をBackgroundOperationTrackerに分離する"
```

## Task 5: 最終検証

- [ ] **Step 1: 全テストを実行**

```sh
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --no-restore 2>&1 | tail -10
```

- [ ] **Step 2: netstandard2.1 ビルドを確認**

```sh
dotnet build src/rosettadds/rosettadds.csproj -f netstandard2.1 --no-restore 2>&1 | tail -5
```

- [ ] **Step 3: Unity .meta 整合性チェック**

```sh
bash .github/scripts/check_unity_meta.sh
```

- [ ] **Step 4: コードレビューを実施**

```sh
git diff main...HEAD
```

- [ ] **Step 5: 指摘を修正してコミット**

- [ ] **Step 6: PR を作成**
