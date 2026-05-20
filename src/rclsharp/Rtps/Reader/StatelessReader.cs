using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Rtps.Reader;

/// <summary>
/// Stateless RTPS Reader。Best-Effort QoS 用。
/// SEDP で match した Writer からの DATA / DATA_FRAG submessage を受信すると、
/// SerializedPayload をハンドラへ届ける。
/// </summary>
public sealed class StatelessReader : IDisposable
{
    private readonly IRtpsTransport? _transport;
    private readonly EntityId _readerEntityId;
    private readonly ILogger _logger;
    private readonly DataFragReassemblyOptions _dataFragOptions;
    private readonly DataFragReassemblyBuffer _dataFragReassembly;
    private readonly object _reassemblyLock = new();
    private readonly object _matchedLock = new();
    private readonly HashSet<Guid> _matchedWriters = new();
    private readonly object _pendingLock = new();
    private readonly Dictionary<Guid, Queue<PendingPayload>> _pendingPayloads = new();
    private int _pendingPayloadCount;
    private readonly object _deliveredLock = new();
    private readonly Dictionary<Guid, DeliveredSequenceWindow> _deliveredSequences = new();
    private readonly object _deliveryLock = new();

    private bool _started;
    private bool _disposed;

    public EntityId ReaderEntityId => _readerEntityId;

    /// <summary>マッチング DATA を受信したときに発火。第二引数は送信元 Participant の GuidPrefix。</summary>
    public event Action<ReadOnlyMemory<byte>, GuidPrefix>? PayloadReceived;

    public StatelessReader(
        EntityId readerEntityId,
        ILogger? logger = null,
        DataFragReassemblyOptions? dataFragOptions = null)
    {
        _transport = null;
        _readerEntityId = readerEntityId;
        _logger = logger ?? NullLogger.Instance;
        _dataFragOptions = dataFragOptions ?? DataFragReassemblyOptions.Default;
        _dataFragReassembly = new DataFragReassemblyBuffer(_dataFragOptions);
    }

    public StatelessReader(
        IRtpsTransport transport,
        EntityId readerEntityId,
        ILogger? logger = null,
        DataFragReassemblyOptions? dataFragOptions = null)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        _transport = transport;
        _readerEntityId = readerEntityId;
        _logger = logger ?? NullLogger.Instance;
        _dataFragOptions = dataFragOptions ?? DataFragReassemblyOptions.Default;
        _dataFragReassembly = new DataFragReassemblyBuffer(_dataFragOptions);
    }

    /// <summary>SEDP で発見した remote Writer を、この Reader の受信対象に追加する。</summary>
    public void MatchWriter(Guid writerGuid)
    {
        ThrowIfDisposed();
        lock (_deliveryLock)
        {
            lock (_matchedLock)
            {
                _matchedWriters.Add(writerGuid);
            }
            foreach (var payload in TakePendingPayloads(writerGuid).OrderBy(static p => p.SequenceNumber.Value))
            {
                if (MarkDelivered(writerGuid, payload.SequenceNumber))
                {
                    PayloadReceived?.Invoke(payload.Payload, payload.SourcePrefix);
                }
            }
        }
    }

    public void UnmatchWriter(Guid writerGuid)
    {
        lock (_matchedLock)
        {
            _matchedWriters.Remove(writerGuid);
        }
        DropPendingPayloads(writerGuid);
        lock (_deliveredLock)
        {
            _deliveredSequences.Remove(writerGuid);
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }
        if (_transport is not null)
        {
            _transport.Received += OnPacket;
        }
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        if (_transport is not null)
        {
            _transport.Received -= OnPacket;
        }
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
            switch (hdr.Kind)
            {
                case SubmessageKind.Data:
                    HandleData(sourcePrefix, body, hdr);
                    break;
                case SubmessageKind.DataFrag:
                    HandleDataFrag(sourcePrefix, body, hdr);
                    break;
                default:
                    break;
            }
        }
    }

    private void HandleData(GuidPrefix sourcePrefix, ReadOnlySpan<byte> body, SubmessageHeader hdr)
    {
        var data = DataSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
        if (!IsTargetReader(data.ReaderEntityId))
        {
            return;
        }
        if (data.SerializedPayload.IsEmpty)
        {
            return;
        }

        var writerGuid = new Guid(sourcePrefix, data.WriterEntityId);
        if (!IsMatchedWriter(writerGuid))
        {
            BufferPendingPayload(writerGuid, data.SerializedPayload, sourcePrefix, data.WriterSequenceNumber);
            return;
        }
        DeliverPayload(writerGuid, data.WriterSequenceNumber, data.SerializedPayload, sourcePrefix);
    }

    private void HandleDataFrag(GuidPrefix sourcePrefix, ReadOnlySpan<byte> body, SubmessageHeader hdr)
    {
        var dataFrag = DataFragSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
        if (!IsTargetReader(dataFrag.ReaderEntityId))
        {
            return;
        }

        var writerGuid = new Guid(sourcePrefix, dataFrag.WriterEntityId);
        byte[]? completedPayload;
        lock (_reassemblyLock)
        {
            completedPayload = _dataFragReassembly.Add(writerGuid, dataFrag);
        }
        if (completedPayload is not null)
        {
            if (!IsMatchedWriter(writerGuid))
            {
                BufferPendingPayload(writerGuid, completedPayload, sourcePrefix, dataFrag.WriterSequenceNumber);
                return;
            }
            DeliverPayload(writerGuid, dataFrag.WriterSequenceNumber, completedPayload, sourcePrefix);
        }
    }

    private bool IsTargetReader(EntityId readerEntityId)
        => readerEntityId.Equals(EntityId.Unknown) || readerEntityId.Equals(_readerEntityId);

    private bool IsMatchedWriter(Guid writerGuid)
    {
        lock (_matchedLock)
        {
            return _matchedWriters.Contains(writerGuid);
        }
    }

    private void BufferPendingPayload(
        Guid writerGuid,
        ReadOnlyMemory<byte> payload,
        GuidPrefix sourcePrefix,
        SequenceNumber sequenceNumber)
    {
        if (payload.Length > _dataFragOptions.MaxSampleSize)
        {
            return;
        }
        if (HasDelivered(writerGuid, sequenceNumber))
        {
            return;
        }

        lock (_pendingLock)
        {
            var now = DateTime.UtcNow;
            RemoveExpiredPending(now);
            EvictOldestPendingIfFull();
            if (!_pendingPayloads.TryGetValue(writerGuid, out var queue))
            {
                queue = new Queue<PendingPayload>();
                _pendingPayloads[writerGuid] = queue;
            }
            if (queue.Any(p => p.SequenceNumber.Equals(sequenceNumber)))
            {
                return;
            }
            queue.Enqueue(new PendingPayload(payload.ToArray(), sourcePrefix, sequenceNumber, now));
            _pendingPayloadCount++;
        }
    }

    private bool HasDelivered(Guid writerGuid, SequenceNumber sequenceNumber)
    {
        lock (_deliveredLock)
        {
            return _deliveredSequences.TryGetValue(writerGuid, out var delivered)
                && delivered.Contains(sequenceNumber.Value);
        }
    }

    private bool MarkDelivered(Guid writerGuid, SequenceNumber sequenceNumber)
    {
        lock (_deliveredLock)
        {
            if (!_deliveredSequences.TryGetValue(writerGuid, out var delivered))
            {
                delivered = new DeliveredSequenceWindow(_dataFragOptions.MaxDeliveredSequenceNumbers);
                _deliveredSequences[writerGuid] = delivered;
            }
            return delivered.Add(sequenceNumber.Value);
        }
    }

    private void DeliverPayload(
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        ReadOnlyMemory<byte> payload,
        GuidPrefix sourcePrefix)
    {
        lock (_deliveryLock)
        {
            if (MarkDelivered(writerGuid, sequenceNumber))
            {
                PayloadReceived?.Invoke(payload, sourcePrefix);
            }
        }
    }

    private IReadOnlyList<PendingPayload> TakePendingPayloads(Guid writerGuid)
    {
        lock (_pendingLock)
        {
            RemoveExpiredPending(DateTime.UtcNow);
            if (!_pendingPayloads.Remove(writerGuid, out var queue))
            {
                return Array.Empty<PendingPayload>();
            }
            _pendingPayloadCount -= queue.Count;
            return queue.ToArray();
        }
    }

    private void DropPendingPayloads(Guid writerGuid)
    {
        lock (_pendingLock)
        {
            if (!_pendingPayloads.Remove(writerGuid, out var queue))
            {
                return;
            }
            _pendingPayloadCount -= queue.Count;
        }
    }

    private void RemoveExpiredPending(DateTime now)
    {
        foreach (var key in _pendingPayloads.Keys.ToArray())
        {
            var queue = _pendingPayloads[key];
            while (queue.Count > 0 && now - queue.Peek().ReceivedAt >= _dataFragOptions.TimeToLive)
            {
                queue.Dequeue();
                _pendingPayloadCount--;
            }
            if (queue.Count == 0)
            {
                _pendingPayloads.Remove(key);
            }
        }
    }

    private void EvictOldestPendingIfFull()
    {
        while (_pendingPayloadCount >= _dataFragOptions.MaxBufferedSamples)
        {
            Guid? oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var (key, queue) in _pendingPayloads)
            {
                if (queue.Count == 0)
                {
                    continue;
                }
                var receivedAt = queue.Peek().ReceivedAt;
                if (receivedAt < oldest)
                {
                    oldest = receivedAt;
                    oldestKey = key;
                }
            }
            if (oldestKey is null)
            {
                _pendingPayloadCount = 0;
                return;
            }
            var selected = _pendingPayloads[oldestKey.Value];
            selected.Dequeue();
            _pendingPayloadCount--;
            if (selected.Count == 0)
            {
                _pendingPayloads.Remove(oldestKey.Value);
            }
        }
    }

    private readonly struct PendingPayload
    {
        public PendingPayload(
            byte[] payload,
            GuidPrefix sourcePrefix,
            SequenceNumber sequenceNumber,
            DateTime receivedAt)
        {
            Payload = payload;
            SourcePrefix = sourcePrefix;
            SequenceNumber = sequenceNumber;
            ReceivedAt = receivedAt;
        }

        public ReadOnlyMemory<byte> Payload { get; }
        public GuidPrefix SourcePrefix { get; }
        public SequenceNumber SequenceNumber { get; }
        public DateTime ReceivedAt { get; }
    }

    private sealed class DeliveredSequenceWindow
    {
        private readonly int _capacity;
        private readonly Queue<long> _order = new();
        private readonly HashSet<long> _set = new();

        public DeliveredSequenceWindow(int capacity)
        {
            _capacity = capacity;
        }

        public bool Contains(long sequenceNumber) => _set.Contains(sequenceNumber);

        public bool Add(long sequenceNumber)
        {
            if (!_set.Add(sequenceNumber))
            {
                return false;
            }

            _order.Enqueue(sequenceNumber);
            while (_order.Count > _capacity)
            {
                _set.Remove(_order.Dequeue());
            }
            return true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
