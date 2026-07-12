using System.Buffers;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// Reliable Stateful RTPS Writer。
/// - <see cref="WriteAsync"/> でサンプルを history に追加し、各 reader proxy へ DATA を送信
/// - <see cref="HeartbeatPeriod"/> 間隔で HEARTBEAT を multicast/unicast に送信
/// - reader からの ACKNACK を <see cref="OnPacketReceived"/> で受け取り、再送要求があれば retransmit
///
/// <para>
/// reader proxy の matching は呼び出し側 (DomainParticipant) が <see cref="MatchReader"/> で明示。
/// </para>
/// </summary>
public sealed class StatefulWriter : IDisposable, IRtpsSubmessageHandler
{
    public const int SendBufferSize = 1500;
    public const int DataFragPayloadSize = 1024;

    private readonly IRtpsTransport _transport;
    private readonly Locator _multicastDestination;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly EntityId _writerEntityId;
    private readonly TimeSpan _heartbeatPeriod;
    private readonly WriterHistoryCache _history;
    private readonly ILogger _logger;
    private readonly bool _purgeAckedSamples;
    private readonly bool _resendHistoryOnMatch;

    private readonly MatchedReaderRegistry _registry = new();
    private readonly BackgroundOperationTracker _tracker;

    private CancellationTokenSource? _cts;
    private Task? _hbLoop;
    private bool _started;
    private bool _disposed;

    public Guid Guid { get; }
    public EntityId WriterEntityId => _writerEntityId;
    public WriterHistoryCache History => _history;
    public TimeSpan HeartbeatPeriod => _heartbeatPeriod;

    public StatefulWriter(
        IRtpsTransport sendTransport,
        Locator multicastDestination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId writerEntityId,
        TimeSpan heartbeatPeriod,
        WriterHistoryCache history,
        ILogger? logger = null,
        bool purgeAckedSamples = true,
        bool resendHistoryOnMatch = false)
    {
        _transport = sendTransport;
        _multicastDestination = multicastDestination;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _writerEntityId = writerEntityId;
        _heartbeatPeriod = heartbeatPeriod;
        _history = history;
        _purgeAckedSamples = purgeAckedSamples;
        _resendHistoryOnMatch = resendHistoryOnMatch;
        _logger = logger ?? NullLogger.Instance;
        _tracker = new BackgroundOperationTracker(_logger);
        Guid = new Guid(localPrefix, writerEntityId);
    }

    /// <summary>
    /// remote reader を match する。
    /// <paramref name="reliability"/> は remote reader の Reliability 種別。
    /// BestEffort reader は ACKNACK を送らないため purge/HB の対象から外される。
    /// 既定値は Reliable (SEDP builtin reader や直接呼び出しの後方互換)。
    /// </summary>
    public void MatchReader(Guid readerGuid, Locator? unicastLocator = null, ReliabilityKind reliability = ReliabilityKind.Reliable)
    {
        ThrowIfDisposed();
        var (proxy, added) = _registry.Match(readerGuid, unicastLocator, reliability);
        if (added)
        {
            if (_resendHistoryOnMatch)
            {
                RunBackground(
                    token => SendHistoricalDataToReaderAsync(proxy, token),
                    "StatefulWriter historical DATA send");
            }
            else if (reliability == ReliabilityKind.Reliable)
            {
                var lastSn = _history.LastSequenceNumber;
                if (lastSn.Value > 0)
                {
                    proxy.SetLowWatermark(lastSn);
                    _logger.Debug(
                        $"StatefulWriter: pre-join low watermark {lastSn} set for reader {readerGuid} (Volatile)");
                }
            }
        }
    }

    public void UnmatchReader(Guid readerGuid)
    {
        _registry.Unmatch(readerGuid);
    }

    public ReaderProxy? GetReaderProxy(Guid readerGuid)
    {
        return _registry.Find(readerGuid);
    }

    public IReadOnlyList<ReaderProxy> MatchedReaders
    {
        get { return _registry.Snapshot(); }
    }

    public int MatchedReaderCount
    {
        get { return _registry.Count; }
    }

    public PublicationMatchedStatus PublicationMatchedStatus
    {
        get { return _registry.TakePublicationMatchedStatus(); }
    }

    /// <summary>
    /// 新規サンプルを history に追加し、全 matched reader へ DATA を送信する。
    /// (HEARTBEAT は周期送信に任せる)
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> serializedPayload, CancellationToken cancellationToken = default)
        => await WriteAsync(serializedPayload, ChangeKind.Alive, cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> serializedPayload,
        ChangeKind kind,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var change = _history.Add(kind, serializedPayload, Time.Now());
        await SendDataAsync(change, cancellationToken).ConfigureAwait(false);
    }

    private CacheChange AddOwnedChange(RtpsPayloadOwner owner, ReadOnlyMemory<byte> payload)
    {
        try
        {
            return _history.Add(ChangeKind.Alive, payload, owner, Time.Now());
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pool-owned payload を 1 件送信。所有権は history に移転し、
    /// history.Add 失敗時のみ owner を release する。
    /// </summary>
    internal async ValueTask WriteOwnedAsync(
        RtpsPayloadOwner owner,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        bool ownerHandedOff = false;
        try
        {
            ThrowIfDisposed();
            var change = AddOwnedChange(owner, payload);
            ownerHandedOff = true;
            await SendDataAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!ownerHandedOff)
            {
                owner.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// <see cref="WriteAsync(ReadOnlyMemory{byte}, ChangeKind, CancellationToken)"/> と同じだが、
    /// 採番された RTPS シーケンス番号を返す。サービスの request/reply 相関に使う。
    /// </summary>
    public ValueTask<SequenceNumber> WriteReturningSequenceNumberAsync(
        ReadOnlyMemory<byte> serializedPayload,
        CancellationToken cancellationToken = default)
        => WriteReturningSequenceNumberAsync(serializedPayload, onSequenceAssigned: null, cancellationToken);

    /// <summary>
    /// サンプルを history に追加して SN を採番し、<paramref name="onSequenceAssigned"/> を
    /// 送信前に同期的に呼んでから DATA を送る。返り値は採番された SN。
    /// サービスの request/reply 相関で、reply 到着前に保留登録を済ませるために使う。
    /// </summary>
    public async ValueTask<SequenceNumber> WriteReturningSequenceNumberAsync(
        ReadOnlyMemory<byte> serializedPayload,
        Action<SequenceNumber>? onSequenceAssigned,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var change = _history.Add(ChangeKind.Alive, serializedPayload, Time.Now());
        onSequenceAssigned?.Invoke(change.SequenceNumber);
        await SendDataAsync(change, cancellationToken).ConfigureAwait(false);
        return change.SequenceNumber;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _hbLoop = Task.Run(() => HeartbeatLoopAsync(token), token);
        _started = true;
    }

    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _hbLoop?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
        catch (Exception ex) { _logger.Warn("StatefulWriter heartbeat loop did not exit cleanly", ex); }
        WaitForBackgroundTasks();
        _cts.Dispose();
        _cts = null;
        _hbLoop = null;
        _started = false;
    }

    private void WaitForBackgroundTasks()
    {
        _tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _history.Dispose();
    }

    /// <summary>
    /// この writer 宛の ACKNACK を含む可能性のあるパケットを処理する。
    /// 通常は transport.Received イベントを購読してこれを呼ぶ。
    /// </summary>
    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed) return;
        try { ProcessPacket(packet); }
        catch (Exception ex) { _logger.Warn($"StatefulWriter failed to parse packet from {source}", ex); }
    }

    public void ProcessPacket(ReadOnlyMemory<byte> packet)
    {
        RtpsMessageDispatcher.Dispatch(packet, _localPrefix, this);
    }

    // IRtpsSubmessageHandler 実装

    void IRtpsSubmessageHandler.OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness) { }
    void IRtpsSubmessageHandler.OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness) { }
    void IRtpsSubmessageHandler.OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb) { }

    void IRtpsSubmessageHandler.OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack)
    {
        if (!ack.WriterEntityId.Equals(_writerEntityId)) return;

        var readerGuid = new Guid(ctx.SourceGuidPrefix, ack.ReaderEntityId);
        var proxy = _registry.Find(readerGuid);
        if (proxy is null) return;

        proxy.ProcessAckNack(ack.ReaderSnState);

        if (_purgeAckedSamples)
        {
            PurgeAckedSamples();
        }

        RunBackground(
            token => ResendRequestedAsync(proxy, token),
            "StatefulWriter requested resend");
    }

    void IRtpsSubmessageHandler.OnGap(in RtpsReceiverContext ctx, GapSubmessage gap) { }

    /// <summary>
    /// reliable な matched reader 全員が ack 済みのサンプルを history から削除する。
    /// BestEffort reader は ACKNACK を送らないため HighestAcked が 0 のままになり、
    /// purge を永久にブロックしてしまう。そのため reliable proxy のみを対象とする。
    /// reliable proxy が 1 つも無い場合は purge しない (best-effort のみのときは MaxSamples eviction に任せる)。
    /// </summary>
    private void PurgeAckedSamples()
    {
        var minAcked = _registry.MinimumReliableAcknowledged();
        if (minAcked is null) return;
        var value = minAcked.Value.Value;
        if (value > 0 && value < long.MaxValue)
        {
            _history.RemoveBelowOrEqual(new SequenceNumber(value));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // 起動直後にも 1 度送る
        await SendHeartbeatToAllAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(_heartbeatPeriod, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            await SendHeartbeatToAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RunBackground(Func<CancellationToken, Task> operation, string operationName)
    {
        if (_disposed) return;
        var token = _cts?.Token ?? CancellationToken.None;
        _tracker.Run(operation, operationName, token);
    }

    private async ValueTask SendHeartbeatToAllAsync(CancellationToken cancellationToken)
    {
        var proxies = _registry.Snapshot();
        if (proxies.Length == 0)
        {
            // matched reader がいないが、multicast に向けて HB を出すこともある (初期発見支援)
            await SendHeartbeatToDestinationAsync(EntityId.Unknown, _multicastDestination, count: 1, cancellationToken).ConfigureAwait(false);
            return;
        }
        // HEARTBEAT は reliable reader のみに送る。BestEffort reader に HB を送っても無意味。
        foreach (var proxy in proxies)
        {
            if (!proxy.IsReliable) continue;
            int count = proxy.IncrementHeartbeatCount();
            var dest = proxy.UnicastLocator ?? _multicastDestination;
            await SendHeartbeatToDestinationAsync(proxy.ReaderGuid.EntityId, dest, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendHeartbeatToDestinationAsync(EntityId readerEntityId, Locator destination, int count, CancellationToken cancellationToken)
    {
        var packet = BuildHeartbeatPacket(readerEntityId, count);
        if (packet.Length == 0)
        {
            return;
        }
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

    /// <summary>HEARTBEAT メッセージを組み立てる (ref struct 使用は同期メソッドに閉じる)。</summary>
    private byte[] BuildHeartbeatPacket(EntityId readerEntityId, int count)
    {
        var first = _history.FirstSequenceNumber;
        var last = _history.LastSequenceNumber;
        // RTPS 仕様: 空 cache でも firstSN=1, lastSN=0 の HB は合法で、
        // reader が writer 状態を確定するために必要。送信をスキップしない。
        if (last.Value == 0)
        {
            // 空 cache: firstSN=1, lastSN=0
            first = new SequenceNumber(1L);
            last = new SequenceNumber(0L);
        }
        else if (first.Value == 0)
        {
            first = new SequenceNumber(1L);
        }

        var hb = new HeartbeatSubmessage(
            readerEntityId, _writerEntityId, first, last, count, final: false, liveliness: false);

        var buffer = new byte[SendBufferSize];
        var msg = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        msg.WriteHeartbeat(hb);
        var packet = new byte[msg.BytesWritten];
        msg.WrittenSpan.CopyTo(packet);
        return packet;
    }

    private async ValueTask SendDataAsync(CacheChange change, CancellationToken cancellationToken)
    {
        var proxies = _registry.Snapshot();
        if (proxies.Length == 0)
        {
            // matched reader がいなければ multicast へ
            await SendDataToDestinationAsync(change, EntityId.Unknown, _multicastDestination, cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (var proxy in proxies)
        {
            var dest = proxy.UnicastLocator ?? _multicastDestination;
            await SendDataToDestinationAsync(change, proxy.ReaderGuid.EntityId, dest, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendHistoricalDataToReaderAsync(ReaderProxy proxy, CancellationToken cancellationToken)
    {
        var first = _history.FirstSequenceNumber;
        var last = _history.LastSequenceNumber;
        if (first.Value == 0 || last.Value == 0 || first > last)
        {
            return;
        }

        var changes = _history.EnumerateRange(first, last);
        foreach (var change in changes)
        {
            var dest = proxy.UnicastLocator ?? _multicastDestination;
            await SendDataToDestinationAsync(change, proxy.ReaderGuid.EntityId, dest, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendDataToDestinationAsync(
        CacheChange change, EntityId readerEntityId, Locator destination, CancellationToken cancellationToken)
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

    private async ValueTask SendDataFragPacketsSequentialAsync(
        CacheChange change,
        EntityId readerEntityId,
        ReadOnlyMemory<byte> inlineQos,
        bool isAlive,
        byte[] scratch,
        Locator destination,
        CancellationToken cancellationToken)
    {
        if (change.SerializedPayload.Length == 0)
        {
            return;
        }
        int firstFragmentCapacity = SendBufferSize
            - RtpsHeader.Size
            - SubmessageHeader.Size
            - DataFragSubmessage.FixedHeaderSize
            - inlineQos.Length;
        int payloadFragmentSize = Math.Min(DataFragPayloadSize, firstFragmentCapacity);
        if (payloadFragmentSize <= 0)
        {
            throw new InvalidOperationException(
                $"DATA_FRAG inline QoS length {inlineQos.Length} leaves no room for payload.");
        }

        int fragmentCount = (change.SerializedPayload.Length + payloadFragmentSize - 1) / payloadFragmentSize;
        ushort fragmentSize = checked((ushort)payloadFragmentSize);
        uint sampleSize = checked((uint)change.SerializedPayload.Length);

        for (int i = 0; i < fragmentCount; i++)
        {
            int offset = i * payloadFragmentSize;
            int length = Math.Min(payloadFragmentSize, change.SerializedPayload.Length - offset);
            var fragmentPayload = change.SerializedPayload.Slice(offset, length);
            var fragmentInlineQos = i == 0 ? inlineQos : default;
            var dataFrag = new DataFragSubmessage(
                readerEntityId: readerEntityId,
                writerEntityId: _writerEntityId,
                writerSn: change.SequenceNumber,
                fragmentStartingNumber: checked((uint)i + 1u),
                fragmentsInSubmessage: 1,
                fragmentSize: fragmentSize,
                sampleSize: sampleSize,
                serializedPayloadFragment: fragmentPayload,
                inlineQos: fragmentInlineQos,
                keyPresent: !isAlive);

            int written = WriteDataFragToScratch(scratch, dataFrag);
            try
            {
                await _transport.SendAsync(scratch.AsMemory(0, written), destination, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Error("StatefulWriter DATA_FRAG send failed", ex);
            }
        }
    }

    /// <summary>DATA メッセージ (INFO_TS + DATA) を組み立てる。</summary>
    private void BuildDataPacket(
        CacheChange change,
        EntityId readerEntityId,
        ReadOnlyMemory<byte> inlineQos,
        bool isAlive,
        Span<byte> destination,
        out int written)
    {
        var writer = new RtpsMessageWriter(destination, _version, _vendorId, _localPrefix);
        writer.WriteInfoTimestamp(new InfoTimestampSubmessage(change.SourceTimestamp));
        var data = new DataSubmessage(
            readerEntityId: readerEntityId,
            writerEntityId: _writerEntityId,
            writerSn: change.SequenceNumber,
            serializedPayload: change.SerializedPayload,
            inlineQos: inlineQos,
            dataPresent: isAlive,
            keyPresent: !isAlive);
        writer.WriteData(data);
        written = writer.BytesWritten;
    }

    private int WriteDataFragToScratch(Span<byte> buffer, DataFragSubmessage dataFrag)
    {
        var writer = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        writer.WriteDataFrag(dataFrag);
        return writer.BytesWritten;
    }

    private static uint ToStatusInfo(ChangeKind kind)
    {
        return kind switch
        {
            ChangeKind.NotAliveDisposed => DataSubmessage.StatusInfoDisposed,
            ChangeKind.NotAliveUnregistered => DataSubmessage.StatusInfoUnregistered,
            ChangeKind.NotAliveDisposedUnregistered =>
                DataSubmessage.StatusInfoDisposed | DataSubmessage.StatusInfoUnregistered,
            _ => 0u,
        };
    }

    private async Task ResendRequestedAsync(ReaderProxy proxy, CancellationToken cancellationToken)
    {
        var requested = proxy.RequestedSequenceNumbers();
        if (requested.Count == 0) return;
        foreach (var sn in requested)
        {
            var dest = proxy.UnicastLocator ?? _multicastDestination;
            if (proxy.IsPreJoin(sn))
            {
                // Volatile pre-join suppression: watermark 以下の SN は DATA ではなく GAP で応答。
                // reader は GAP 受理後、当該 SN を NACK しなくなる。
                await SendGapToDestinationAsync(
                    sn,
                    proxy.ReaderGuid.EntityId,
                    dest,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var change = _history.Get(sn);
                if (change is null)
                {
                    await SendGapToDestinationAsync(
                        sn,
                        proxy.ReaderGuid.EntityId,
                        dest,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendDataToDestinationAsync(change, proxy.ReaderGuid.EntityId, dest, cancellationToken).ConfigureAwait(false);
                }
            }
            proxy.ClearRequested(sn);
        }
    }

    private async ValueTask SendGapToDestinationAsync(
        SequenceNumber missingSequenceNumber,
        EntityId readerEntityId,
        Locator destination,
        CancellationToken cancellationToken)
    {
        var packet = BuildGapPacket(missingSequenceNumber, readerEntityId);
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

    private byte[] BuildGapPacket(SequenceNumber missingSequenceNumber, EntityId readerEntityId)
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
        return packet;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
