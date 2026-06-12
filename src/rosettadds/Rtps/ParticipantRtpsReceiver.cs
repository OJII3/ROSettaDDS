using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rtps;

/// <summary>
/// Participant 単位の単一 RTPS Receiver。
/// <para>
/// 購読した全 transport の <see cref="IRtpsTransport.Received"/> を 1 経路に集約し、
/// 受信パケットを <see cref="RtpsMessageDispatcher"/> で 1 度だけパースする。
/// 各 submessage は宛先 EntityId (reader / writer) に応じて登録済みの
/// <see cref="IRtpsSubmessageHandler"/> へ fan-out する。
/// </para>
/// <para>
/// reader 宛 submessage (DATA / DATA_FRAG / HEARTBEAT / GAP) の readerEntityId が
/// ENTITYID_UNKNOWN の場合は全 reader へブロードキャストする (各 reader が自身の matched
/// writer で内部フィルタする)。具体的な reader id を持つ場合はその reader にのみ転送する。
/// writer 宛 submessage (ACKNACK) は writerEntityId で対象 writer を特定する。
/// </para>
/// </summary>
public sealed class ParticipantRtpsReceiver : IRtpsSubmessageHandler, IDisposable
{
    private readonly GuidPrefix _localPrefix;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<EntityId, IRtpsSubmessageHandler> _readers = new();
    private readonly Dictionary<EntityId, IRtpsSubmessageHandler> _writers = new();
    private readonly List<IRtpsTransport> _transports = new();
    private IRtpsSubmessageHandler[] _readerBroadcast = Array.Empty<IRtpsSubmessageHandler>();
    private bool _disposed;

    public ParticipantRtpsReceiver(GuidPrefix localPrefix, ILogger? logger = null)
    {
        _localPrefix = localPrefix;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>transport の受信イベントをこの receiver に接続する。</summary>
    public void Subscribe(IRtpsTransport transport)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        lock (_lock)
        {
            if (_transports.Contains(transport))
            {
                return;
            }
            _transports.Add(transport);
        }
        transport.Received += OnPacketReceived;
    }

    /// <summary>購読中の全 transport の受信イベントを切断する。</summary>
    public void UnsubscribeAll()
    {
        IRtpsTransport[] transports;
        lock (_lock)
        {
            transports = _transports.ToArray();
            _transports.Clear();
        }
        foreach (var transport in transports)
        {
            transport.Received -= OnPacketReceived;
        }
    }

    /// <summary>reader (DATA/DATA_FRAG/HEARTBEAT/GAP の宛先) を登録する。</summary>
    public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_lock)
        {
            _readers[readerEntityId] = handler;
            RebuildReaderBroadcastLocked();
        }
    }

    public void UnregisterReader(EntityId readerEntityId)
    {
        lock (_lock)
        {
            if (_readers.Remove(readerEntityId))
            {
                RebuildReaderBroadcastLocked();
            }
        }
    }

    /// <summary>writer (ACKNACK の宛先) を登録する。</summary>
    public void RegisterWriter(EntityId writerEntityId, IRtpsSubmessageHandler handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_lock)
        {
            _writers[writerEntityId] = handler;
        }
    }

    public void UnregisterWriter(EntityId writerEntityId)
    {
        lock (_lock)
        {
            _writers.Remove(writerEntityId);
        }
    }

    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            RtpsMessageDispatcher.Dispatch(packet, _localPrefix, this);
        }
        catch (Exception ex)
        {
            _logger.Warn($"ParticipantRtpsReceiver failed to parse packet from {source}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IRtpsTransport[] transports;
        lock (_lock)
        {
            transports = _transports.ToArray();
            _transports.Clear();
            _readers.Clear();
            _writers.Clear();
            _readerBroadcast = Array.Empty<IRtpsSubmessageHandler>();
        }
        foreach (var transport in transports)
        {
            transport.Received -= OnPacketReceived;
        }
    }

    // IRtpsSubmessageHandler: 各 submessage を宛先ハンドラへ fan-out する

    void IRtpsSubmessageHandler.OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness)
    {
        if (TryGetReader(data.ReaderEntityId, out var reader))
        {
            reader.OnData(in ctx, data, endianness);
            return;
        }
        if (data.ReaderEntityId.Equals(EntityId.Unknown))
        {
            foreach (var handler in Volatile.Read(ref _readerBroadcast))
            {
                handler.OnData(in ctx, data, endianness);
            }
        }
    }

    void IRtpsSubmessageHandler.OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness)
    {
        if (TryGetReader(dataFrag.ReaderEntityId, out var reader))
        {
            reader.OnDataFrag(in ctx, dataFrag, endianness);
            return;
        }
        if (dataFrag.ReaderEntityId.Equals(EntityId.Unknown))
        {
            foreach (var handler in Volatile.Read(ref _readerBroadcast))
            {
                handler.OnDataFrag(in ctx, dataFrag, endianness);
            }
        }
    }

    void IRtpsSubmessageHandler.OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb)
    {
        if (TryGetReader(hb.ReaderEntityId, out var reader))
        {
            reader.OnHeartbeat(in ctx, hb);
            return;
        }
        if (hb.ReaderEntityId.Equals(EntityId.Unknown))
        {
            foreach (var handler in Volatile.Read(ref _readerBroadcast))
            {
                handler.OnHeartbeat(in ctx, hb);
            }
        }
    }

    void IRtpsSubmessageHandler.OnGap(in RtpsReceiverContext ctx, GapSubmessage gap)
    {
        if (TryGetReader(gap.ReaderEntityId, out var reader))
        {
            reader.OnGap(in ctx, gap);
            return;
        }
        if (gap.ReaderEntityId.Equals(EntityId.Unknown))
        {
            foreach (var handler in Volatile.Read(ref _readerBroadcast))
            {
                handler.OnGap(in ctx, gap);
            }
        }
    }

    void IRtpsSubmessageHandler.OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack)
    {
        IRtpsSubmessageHandler? writer;
        lock (_lock)
        {
            _writers.TryGetValue(ack.WriterEntityId, out writer);
        }
        writer?.OnAckNack(in ctx, ack);
    }

    private bool TryGetReader(EntityId readerEntityId, out IRtpsSubmessageHandler handler)
    {
        if (readerEntityId.Equals(EntityId.Unknown))
        {
            handler = null!;
            return false;
        }
        lock (_lock)
        {
            return _readers.TryGetValue(readerEntityId, out handler!);
        }
    }

    private void RebuildReaderBroadcastLocked()
    {
        var snapshot = new IRtpsSubmessageHandler[_readers.Count];
        _readers.Values.CopyTo(snapshot, 0);
        Volatile.Write(ref _readerBroadcast, snapshot);
    }
}
