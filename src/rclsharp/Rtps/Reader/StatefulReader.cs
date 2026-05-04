using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.HistoryCache;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Rtps.Reader;

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

    private readonly object _matchedLock = new();
    private readonly Dictionary<Guid, WriterProxy> _matched = new();

    private bool _disposed;

    public Guid Guid { get; }
    public EntityId ReaderEntityId => _readerEntityId;

    /// <summary>新規 (非重複) サンプルを受信したときに発火。</summary>
    public event Action<CacheChange>? PayloadReceived;

    public StatefulReader(
        IRtpsTransport replyTransport,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId readerEntityId,
        Locator ackNackFallbackDestination,
        ILogger? logger = null)
    {
        _replyTransport = replyTransport;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _readerEntityId = readerEntityId;
        _ackNackFallbackDestination = ackNackFallbackDestination;
        _logger = logger ?? NullLogger.Instance;
        Guid = new Guid(localPrefix, readerEntityId);
    }

    public void MatchWriter(Guid writerGuid, Locator? unicastReplyLocator = null)
    {
        ThrowIfDisposed();
        lock (_matchedLock)
        {
            if (!_matched.ContainsKey(writerGuid))
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
        try { ProcessPacket(packet.Span); }
        catch (Exception ex) { _logger.Warn($"StatefulReader failed to parse packet from {source}", ex); }
    }

    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
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
                                Time.Zero, // INFO_TS は今回参照しない (Phase 7 簡易)
                                data.SerializedPayload);
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

                        // ACKNACK パケット組立
                        var ackPacket = BuildAckNackPacket(proxy);
                        pendingAcknacks ??= new List<(WriterProxy, byte[])>();
                        pendingAcknacks.Add((proxy, ackPacket));
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
                var dest = proxy.UnicastReplyLocator ?? _ackNackFallbackDestination;
                _ = _replyTransport.SendAsync(packetBytes, dest, CancellationToken.None);
            }
        }
    }

    private static ChangeKind ToChangeKind(DataSubmessage data, CdrEndianness endianness)
    {
        if (!DataSubmessage.TryReadStatusInfo(data.InlineQos.Span, endianness, out var statusInfo))
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

    private byte[] BuildAckNackPacket(WriterProxy proxy)
    {
        int count = proxy.IncrementAckNackCount();
        var snSet = proxy.BuildAckNackBitmap();

        // Final=true: 仕様上は Reliable で false が一般的だが、Phase 7 では応答必須を意味する false でも true でも実装に害なし
        var ack = new AckNackSubmessage(
            readerEntityId: _readerEntityId,
            writerEntityId: proxy.WriterGuid.EntityId,
            readerSnState: snSet,
            count: count,
            final: false);

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
