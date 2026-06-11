using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
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
public sealed class StatefulReader : IDisposable
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

    private bool _disposed;

    public Guid Guid { get; }
    public EntityId ReaderEntityId => _readerEntityId;

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

    /// <summary>transport.Received を購読してこれを呼ぶ。</summary>
    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed)
        {
            return;
        }

        try { ProcessPacketBorrowed(packet); }
        catch (Exception ex) { _logger.Warn($"StatefulReader failed to parse packet from {source}", ex); }
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (_disposed)
        {
            return;
        }

        if (!RtpsHeader.TryRead(packet, out _, out _, out var sourcePrefix)) return;
        var reader = new RtpsMessageReader(packet);
        // 同 message 内で生成された ACKNACK を一旦バッファリングしてから送る (async と ref struct を分離)
        List<(WriterProxy proxy, byte[] packet)>? pendingAcknacks = null;

        while (reader.TryReadNext(out var hdr, out var body))
        {
            switch (hdr.Kind)
            {
                case SubmessageKind.Data:
                    {
                        var data = DataSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
                        // この reader 宛 (Unknown も許容、multicast 用途) のみ
                        if (!data.ReaderEntityId.Equals(EntityId.Unknown)
                            && !data.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, data.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;
                        if (data.SerializedPayload.IsEmpty) continue;

                        bool isNew = proxy.MarkReceived(data.WriterSequenceNumber);
                        if (isNew)
                        {
                            var kind = ToChangeKind(data, hdr.Endianness);
                            var change = new CacheChange(
                                kind,
                                writerGuid,
                                data.WriterSequenceNumber,
                                Time.Zero,
                                data.SerializedPayload);
                            PayloadReceived?.Invoke(change);
                        }
                        break;
                    }
                case SubmessageKind.DataFrag:
                    {
                        var dataFrag = DataFragSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
                        if (!dataFrag.ReaderEntityId.Equals(EntityId.Unknown)
                            && !dataFrag.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, dataFrag.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        DataFragReassemblyResult? completed;
                        lock (_reassemblyLock)
                        {
                            completed = _dataFragReassembly.Add(writerGuid, dataFrag, hdr.Endianness);
                        }
                        if (completed is null) continue;

                        bool isNew = proxy.MarkReceived(dataFrag.WriterSequenceNumber);
                        if (isNew)
                        {
                            var kind = ToChangeKind(completed.Value.InlineQos.Span, completed.Value.InlineQosEndianness);
                            var change = new CacheChange(
                                kind,
                                writerGuid,
                                dataFrag.WriterSequenceNumber,
                                Time.Zero,
                                completed.Value.Payload);
                            PayloadReceived?.Invoke(change);
                        }
                        break;
                    }
                case SubmessageKind.Heartbeat:
                    {
                        var hb = HeartbeatSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
                        if (!hb.ReaderEntityId.Equals(EntityId.Unknown)
                            && !hb.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, hb.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        proxy.UpdateHeartbeatRange(hb.FirstSequenceNumber, hb.LastSequenceNumber);

                        // RTPS 8.4.12 / 9.4.5 に準拠した ACKNACK 送信判定:
                        // - 欠損あり       → final=false で送信 (再送を要求)
                        // - 欠損なし かつ HB.Final=false → final=true で送信 (pure ack、往復増幅を防ぐ)
                        // - 欠損なし かつ HB.Final=true  → 送信しない (writer は応答不要と通知)
                        bool hasMissing = proxy.HasMissingSequences();
                        if (hasMissing)
                        {
                            var ackPacket = BuildAckNackPacket(proxy, final: false);
                            pendingAcknacks ??= new List<(WriterProxy, byte[])>();
                            pendingAcknacks.Add((proxy, ackPacket));
                        }
                        else if (!hb.Final)
                        {
                            var ackPacket = BuildAckNackPacket(proxy, final: true);
                            pendingAcknacks ??= new List<(WriterProxy, byte[])>();
                            pendingAcknacks.Add((proxy, ackPacket));
                        }
                        // hb.Final=true かつ 欠損なし → ACKNACK 不要
                        break;
                    }
                case SubmessageKind.Gap:
                    {
                        var gap = GapSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
                        if (!gap.ReaderEntityId.Equals(EntityId.Unknown)
                            && !gap.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, gap.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        proxy.MarkGap(gap.GapStart, gap.GapList);
                        break;
                    }
                default:
                    break;
            }
        }

        // バッファした ACKNACK を送信 (同期 fire-and-forget)
        if (pendingAcknacks is not null)
        {
            foreach (var (proxy, packetBytes) in pendingAcknacks)
            {
                if (_disposed)
                {
                    return;
                }

                var dest = proxy.UnicastReplyLocator ?? _ackNackFallbackDestination;
                _ = SendAckNackAsync(packetBytes, dest);
            }
        }
    }

    private void ProcessPacketBorrowed(ReadOnlyMemory<byte> packet)
    {
        if (_disposed)
        {
            return;
        }

        if (!RtpsHeader.TryRead(packet.Span, out _, out _, out var sourcePrefix)) return;
        var reader = RtpsMessageReader.FromMemory(packet);
        List<(WriterProxy proxy, byte[] packet)>? pendingAcknacks = null;

        while (reader.TryReadNextMemory(out var hdr, out var body))
        {
            switch (hdr.Kind)
            {
                case SubmessageKind.Data:
                    {
                        var data = DataSubmessage.ReadBodyBorrowed(body, hdr.Endianness, hdr.Flags);
                        if (!data.ReaderEntityId.Equals(EntityId.Unknown)
                            && !data.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, data.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;
                        if (data.SerializedPayload.IsEmpty) continue;

                        bool isNew = proxy.MarkReceived(data.WriterSequenceNumber);
                        if (isNew)
                        {
                            var kind = ToChangeKind(data, hdr.Endianness);
                            var change = new CacheChange(
                                kind,
                                writerGuid,
                                data.WriterSequenceNumber,
                                Time.Zero,
                                data.SerializedPayload);
                            PayloadReceived?.Invoke(change);
                        }
                        break;
                    }
                case SubmessageKind.DataFrag:
                    {
                        var dataFrag = DataFragSubmessage.ReadBodyBorrowed(body, hdr.Endianness, hdr.Flags);
                        if (!dataFrag.ReaderEntityId.Equals(EntityId.Unknown)
                            && !dataFrag.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, dataFrag.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        DataFragReassemblyResult? completed;
                        lock (_reassemblyLock)
                        {
                            completed = _dataFragReassembly.Add(writerGuid, dataFrag, hdr.Endianness);
                        }
                        if (completed is null) continue;

                        bool isNew = proxy.MarkReceived(dataFrag.WriterSequenceNumber);
                        if (isNew)
                        {
                            var kind = ToChangeKind(completed.Value.InlineQos.Span, completed.Value.InlineQosEndianness);
                            var change = new CacheChange(
                                kind,
                                writerGuid,
                                dataFrag.WriterSequenceNumber,
                                Time.Zero,
                                completed.Value.Payload);
                            PayloadReceived?.Invoke(change);
                        }
                        break;
                    }
                case SubmessageKind.Heartbeat:
                    {
                        var hb = HeartbeatSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                        if (!hb.ReaderEntityId.Equals(EntityId.Unknown)
                            && !hb.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, hb.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        proxy.UpdateHeartbeatRange(hb.FirstSequenceNumber, hb.LastSequenceNumber);

                        // RTPS 8.4.12 / 9.4.5 に準拠した ACKNACK 送信判定 (ProcessPacketBorrowed 版):
                        // - 欠損あり       → final=false で送信 (再送を要求)
                        // - 欠損なし かつ HB.Final=false → final=true で送信 (pure ack、往復増幅を防ぐ)
                        // - 欠損なし かつ HB.Final=true  → 送信しない (writer は応答不要と通知)
                        bool hasMissing = proxy.HasMissingSequences();
                        if (hasMissing)
                        {
                            var ackPacket = BuildAckNackPacket(proxy, final: false);
                            pendingAcknacks ??= new List<(WriterProxy, byte[])>();
                            pendingAcknacks.Add((proxy, ackPacket));
                        }
                        else if (!hb.Final)
                        {
                            var ackPacket = BuildAckNackPacket(proxy, final: true);
                            pendingAcknacks ??= new List<(WriterProxy, byte[])>();
                            pendingAcknacks.Add((proxy, ackPacket));
                        }
                        // hb.Final=true かつ 欠損なし → ACKNACK 不要
                        break;
                    }
                case SubmessageKind.Gap:
                    {
                        var gap = GapSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                        if (!gap.ReaderEntityId.Equals(EntityId.Unknown)
                            && !gap.ReaderEntityId.Equals(_readerEntityId))
                        {
                            continue;
                        }
                        var writerGuid = new Guid(sourcePrefix, gap.WriterEntityId);
                        WriterProxy? proxy;
                        lock (_matchedLock) { _matched.TryGetValue(writerGuid, out proxy); }
                        if (proxy is null) continue;

                        proxy.MarkGap(gap.GapStart, gap.GapList);
                        break;
                    }
                default:
                    break;
            }
        }

        if (pendingAcknacks is not null)
        {
            foreach (var (proxy, packetBytes) in pendingAcknacks)
            {
                if (_disposed)
                {
                    return;
                }

                var dest = proxy.UnicastReplyLocator ?? _ackNackFallbackDestination;
                _ = SendAckNackAsync(packetBytes, dest);
            }
        }
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
