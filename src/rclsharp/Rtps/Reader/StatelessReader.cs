using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Rtps.Reader;

/// <summary>
/// Stateless RTPS Reader。Best-Effort QoS 用。
/// 指定 writerEntityId に一致する DATA submessage を受信すると、
/// SerializedPayload をハンドラへ届ける。順序/重複の追跡は行わない。
/// </summary>
public sealed class StatelessReader : IDisposable
{
    private readonly IRtpsTransport _transport;
    private readonly EntityId _writerEntityId;
    private readonly ILogger _logger;
    private readonly object _matchedLock = new();
    private readonly HashSet<Guid> _matchedWriters = new();

    private bool _started;
    private bool _disposed;

    public EntityId WriterEntityId => _writerEntityId;

    /// <summary>マッチング DATA を受信したときに発火。第二引数は送信元 Participant の GuidPrefix。</summary>
    public event Action<ReadOnlyMemory<byte>, GuidPrefix>? PayloadReceived;

    public StatelessReader(IRtpsTransport transport, EntityId writerEntityId, ILogger? logger = null)
    {
        _transport = transport;
        _writerEntityId = writerEntityId;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>SEDP で発見した remote Writer を、この Reader の受信対象に追加する。</summary>
    public void MatchWriter(Guid writerGuid)
    {
        ThrowIfDisposed();
        lock (_matchedLock)
        {
            _matchedWriters.Add(writerGuid);
        }
    }

    public void UnmatchWriter(Guid writerGuid)
    {
        lock (_matchedLock)
        {
            _matchedWriters.Remove(writerGuid);
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }
        _transport.Received += OnPacket;
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _transport.Received -= OnPacket;
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            ProcessPacket(packet.Span);
        }
        catch (Exception ex)
        {
            _logger.Warn($"StatelessReader failed to parse packet from {source}", ex);
        }
    }

    private void OnPacket(ReadOnlyMemory<byte> packet, Locator source)
        => OnPacketReceived(packet, source);

    /// <summary>パケットを RTPS message として解釈し、マッチする DATA を上位へ転送する。</summary>
    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (_disposed)
        {
            return;
        }
        if (!RtpsHeader.TryRead(packet, out _, out _, out var sourcePrefix))
        {
            return;
        }
        var reader = new RtpsMessageReader(packet);
        while (reader.TryReadNext(out var hdr, out var body))
        {
            if (hdr.Kind != SubmessageKind.Data)
            {
                continue;
            }
            var data = DataSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
            if (!IsMatchedWriter(sourcePrefix, data.WriterEntityId))
            {
                continue;
            }
            if (data.SerializedPayload.IsEmpty)
            {
                continue;
            }
            PayloadReceived?.Invoke(data.SerializedPayload, sourcePrefix);
        }
    }

    private bool IsMatchedWriter(GuidPrefix sourcePrefix, EntityId writerEntityId)
    {
        lock (_matchedLock)
        {
            if (_matchedWriters.Contains(new Guid(sourcePrefix, writerEntityId)))
            {
                return true;
            }
        }

        // SEDP がまだ届いていない rclsharp 同士の旧経路では、topic hash 由来の writer EntityId を使う。
        return writerEntityId.Equals(_writerEntityId);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
