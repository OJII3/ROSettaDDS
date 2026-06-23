using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Reader;

/// <summary>
/// Reliable Stateful RTPS Reader。
/// - DATA を受信して重複排除し、新規サンプルを <see cref="PayloadReceived"/> で上位へ届ける
/// - HEARTBEAT を受信したら ACKNACK を返す (欠損 SN を bitmap で要求)
/// - matching は呼び出し側 (DomainParticipant) が <see cref="MatchWriter"/> で明示
/// </summary>
public sealed class StatefulReader : IDisposable, IRtpsSubmessageHandler
{
    public const int SendBufferSize = 1500;

    private readonly IRtpsTransport _replyTransport;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly EntityId _readerEntityId;
    private readonly Locator _ackNackFallbackDestination;
    private readonly ILogger _logger;
    private readonly DataFragReassemblyBuffer _dataFragReassembly;
    private readonly object _reassemblyLock = new();

    private readonly object _matchedLock = new();
    private readonly Dictionary<Guid, WriterProxy> _matched = new();
    private long _totalMatchedWriters;
    private Guid? _lastPublicationHandle;
    private int _lastReportedCurrentWriters;
    private long _lastReportedTotalWriters;

    private bool _disposed;

    private long _dataSubmessagesReceived;
    private long _dataFragSubmessagesReceived;
    private long _reassembledPayloads;
    private long _payloadsDelivered;
    private long _payloadsDropped;

    public Guid Guid { get; }
    public EntityId ReaderEntityId => _readerEntityId;

    public RtpsReaderDiagnostics Diagnostics => new(
        Volatile.Read(ref _dataSubmessagesReceived),
        Volatile.Read(ref _dataFragSubmessagesReceived),
        Volatile.Read(ref _reassembledPayloads),
        Volatile.Read(ref _payloadsDelivered),
        payloadsBufferedPendingMatch: 0,
        Volatile.Read(ref _payloadsDropped));

    /// <summary>
    /// 新規 (非重複) サンプルを受信したときに発火。
    /// <see cref="CacheChange.SerializedPayload"/> は呼び出し中のみ有効な場合があるため、
    /// 保持する場合は呼び出し側で複製する。
    /// </summary>
    public event Action<CacheChange>? PayloadReceived;

    public StatefulReader(
        IRtpsTransport replyTransport,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId readerEntityId,
        Locator ackNackFallbackDestination,
        ILogger? logger = null,
        DataFragReassemblyOptions? dataFragOptions = null)
    {
        _replyTransport = replyTransport;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _readerEntityId = readerEntityId;
        _ackNackFallbackDestination = ackNackFallbackDestination;
        _logger = logger ?? NullLogger.Instance;
        _dataFragReassembly = new DataFragReassemblyBuffer(dataFragOptions);
        Guid = new Guid(localPrefix, readerEntityId);
    }

    public void MatchWriter(Guid writerGuid, Locator? unicastReplyLocator = null)
    {
        ThrowIfDisposed();
        lock (_matchedLock)
        {
            if (_matched.TryGetValue(writerGuid, out var existing))
            {
                existing.UpdateUnicastReplyLocator(unicastReplyLocator);
            }
            else
            {
                _matched[writerGuid] = new WriterProxy(writerGuid, unicastReplyLocator);
                _totalMatchedWriters++;
                _lastPublicationHandle = writerGuid;
            }
        }
    }

    public void UnmatchWriter(Guid writerGuid)
    {
        lock (_matchedLock) { _matched.Remove(writerGuid); }
    }

    public WriterProxy? GetWriterProxy(Guid writerGuid)
    {
        lock (_matchedLock) { return _matched.TryGetValue(writerGuid, out var p) ? p : null; }
    }

    public IReadOnlyList<WriterProxy> MatchedWriters
    {
        get { lock (_matchedLock) { return _matched.Values.ToArray(); } }
    }

    public int MatchedWriterCount
    {
        get { lock (_matchedLock) { return _matched.Count; } }
    }

    public SubscriptionMatchedStatus SubscriptionMatchedStatus
    {
        get
        {
            int current;
            long total;
            int currentChange;
            long totalChange;
            Guid? lastHandle;
            lock (_matchedLock)
            {
                current = _matched.Count;
                total = _totalMatchedWriters;
                lastHandle = _lastPublicationHandle;
                currentChange = current - _lastReportedCurrentWriters;
                totalChange = total - _lastReportedTotalWriters;
                _lastReportedCurrentWriters = current;
                _lastReportedTotalWriters = total;
            }
            return new SubscriptionMatchedStatus
            {
                CurrentCount = current,
                CurrentCountChange = currentChange,
                TotalCount = checked((int)Math.Min(total, int.MaxValue)),
                TotalCountChange = checked((int)Math.Min(totalChange, int.MaxValue)),
                LastPublicationHandle = lastHandle,
            };
        }
    }

    /// <summary>transport.Received を購読してこれを呼ぶ。</summary>
    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed)
        {
            return;
        }

        try { ProcessPacket(packet); }
        catch (Exception ex) { _logger.Warn($"StatefulReader failed to parse packet from {source}", ex); }
    }

    /// <summary>パケットを RTPS message として解釈し、マッチする submessage を処理する。</summary>
    public void ProcessPacket(ReadOnlyMemory<byte> packet)
    {
        if (_disposed)
        {
            return;
        }

        RtpsMessageDispatcher.Dispatch(packet, _localPrefix, this);
    }

    // IRtpsSubmessageHandler 実装

    void IRtpsSubmessageHandler.OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness)
    {
        if (!data.ReaderEntityId.Equals(EntityId.Unknown)
            && !data.ReaderEntityId.Equals(_readerEntityId))
        {
            return;
        }
        Interlocked.Increment(ref _dataSubmessagesReceived);
        var writerGuid = new Guid(ctx.SourceGuidPrefix, data.WriterEntityId);
        WriterProxy? proxy;
        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
        if (proxy is null) { Interlocked.Increment(ref _payloadsDropped); return; }
        if (data.SerializedPayload.IsEmpty) { Interlocked.Increment(ref _payloadsDropped); return; }

        bool isNew = proxy.MarkReceived(data.WriterSequenceNumber);
        if (isNew)
        {
            var kind = ToChangeKind(data, endianness);
            var change = new CacheChange(
                kind,
                writerGuid,
                data.WriterSequenceNumber,
                ctx.Timestamp ?? Time.Zero,
                data.SerializedPayload,
                data.InlineQos,
                endianness);
            PayloadReceived?.Invoke(change);
            Interlocked.Increment(ref _payloadsDelivered);
        }
        else
        {
            Interlocked.Increment(ref _payloadsDropped);
        }
    }

    void IRtpsSubmessageHandler.OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness)
    {
        if (!dataFrag.ReaderEntityId.Equals(EntityId.Unknown)
            && !dataFrag.ReaderEntityId.Equals(_readerEntityId))
        {
            return;
        }
        Interlocked.Increment(ref _dataFragSubmessagesReceived);
        var writerGuid = new Guid(ctx.SourceGuidPrefix, dataFrag.WriterEntityId);
        WriterProxy? proxy;
        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
        if (proxy is null) { Interlocked.Increment(ref _payloadsDropped); return; }

        DataFragReassemblyResult? completed;
        lock (_reassemblyLock)
        {
            completed = _dataFragReassembly.Add(writerGuid, dataFrag, endianness);
        }
        if (completed is null) return;
        Interlocked.Increment(ref _reassembledPayloads);

        bool isNew = proxy.MarkReceived(dataFrag.WriterSequenceNumber);
        if (isNew)
        {
            var kind = ToChangeKind(completed.Value.InlineQos.Span, completed.Value.InlineQosEndianness);
            var change = new CacheChange(
                kind,
                writerGuid,
                dataFrag.WriterSequenceNumber,
                ctx.Timestamp ?? Time.Zero,
                completed.Value.Payload,
                completed.Value.InlineQos,
                completed.Value.InlineQosEndianness);
            PayloadReceived?.Invoke(change);
            Interlocked.Increment(ref _payloadsDelivered);
        }
        else
        {
            Interlocked.Increment(ref _payloadsDropped);
        }
    }

    void IRtpsSubmessageHandler.OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb)
    {
        if (!hb.ReaderEntityId.Equals(EntityId.Unknown)
            && !hb.ReaderEntityId.Equals(_readerEntityId))
        {
            return;
        }
        var writerGuid = new Guid(ctx.SourceGuidPrefix, hb.WriterEntityId);
        WriterProxy? proxy;
        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
        if (proxy is null) return;

        proxy.UpdateHeartbeatRange(hb.FirstSequenceNumber, hb.LastSequenceNumber);

        // RTPS 8.4.12 / 9.4.5 に準拠した ACKNACK 送信判定:
        // - 欠損あり       → final=false で送信 (再送を要求)
        // - 欠損なし かつ HB.Final=false → final=true で送信 (pure ack、往復増幅を防ぐ)
        // - 欠損なし かつ HB.Final=true  → 送信しない (writer は応答不要と通知)
        bool hasMissing = proxy.HasMissingSequences();
        byte[] ackPacket;
        if (hasMissing)
        {
            ackPacket = BuildAckNackPacket(proxy, final: false);
        }
        else if (!hb.Final)
        {
            ackPacket = BuildAckNackPacket(proxy, final: true);
        }
        else
        {
            // hb.Final=true かつ 欠損なし → ACKNACK 不要
            return;
        }

        var dest = proxy.UnicastReplyLocator ?? _ackNackFallbackDestination;
        _ = SendAckNackAsync(ackPacket, dest);
    }

    void IRtpsSubmessageHandler.OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack) { }

    void IRtpsSubmessageHandler.OnGap(in RtpsReceiverContext ctx, GapSubmessage gap)
    {
        if (!gap.ReaderEntityId.Equals(EntityId.Unknown)
            && !gap.ReaderEntityId.Equals(_readerEntityId))
        {
            return;
        }
        var writerGuid = new Guid(ctx.SourceGuidPrefix, gap.WriterEntityId);
        WriterProxy? proxy;
        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
        if (proxy is null) return;

        proxy.MarkGap(gap.GapStart, gap.GapList);
    }

    private async Task SendAckNackAsync(byte[] packetBytes, Locator destination)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _replyTransport.SendAsync(packetBytes, destination, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn("StatefulReader ACKNACK send failed", ex);
        }
    }

    private static ChangeKind ToChangeKind(DataSubmessage data, CdrEndianness endianness)
        => ToChangeKind(data.InlineQos.Span, endianness);

    private static ChangeKind ToChangeKind(ReadOnlySpan<byte> inlineQos, CdrEndianness endianness)
    {
        if (!DataSubmessage.TryReadStatusInfo(inlineQos, endianness, out var statusInfo))
        {
            return ChangeKind.Alive;
        }
        bool disposed = (statusInfo & DataSubmessage.StatusInfoDisposed) != 0;
        bool unregistered = (statusInfo & DataSubmessage.StatusInfoUnregistered) != 0;
        if (disposed && unregistered)
        {
            return ChangeKind.NotAliveDisposedUnregistered;
        }
        if (disposed)
        {
            return ChangeKind.NotAliveDisposed;
        }
        return unregistered ? ChangeKind.NotAliveUnregistered : ChangeKind.Alive;
    }

    private byte[] BuildAckNackPacket(WriterProxy proxy, bool final)
    {
        int count = proxy.IncrementAckNackCount();
        var snSet = proxy.BuildAckNackBitmap();

        var ack = new AckNackSubmessage(
            readerEntityId: _readerEntityId,
            writerEntityId: proxy.WriterGuid.EntityId,
            readerSnState: snSet,
            count: count,
            final: final);

        var buffer = new byte[SendBufferSize];
        var msg = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        msg.WriteAckNack(ack);
        var packet = new byte[msg.BytesWritten];
        msg.WrittenSpan.CopyTo(packet);
        return packet;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
