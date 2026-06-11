using System.Buffers;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Discovery;

/// <summary>
/// SPDP の builtin participant writer。
/// 周期的に <see cref="ParticipantData"/> を PL_CDR でシリアライズし、
/// RTPS メッセージ (INFO_TS + DATA) として multicast Locator へ送信する。
/// </summary>
public sealed class SpdpBuiltinParticipantWriter : IDisposable
{
    /// <summary>RTPS メッセージ送信用バッファサイズ (1500 = MTU 想定)。</summary>
    public const int SendBufferSize = 1500;

    private readonly IRtpsTransport _transport;
    private readonly Locator _multicastDestination;
    private readonly TimeSpan _interval;
    private readonly Func<ParticipantData> _participantDataProvider;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly ILogger _logger;

    private long _sequenceNumber;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    public SpdpBuiltinParticipantWriter(
        IRtpsTransport transport,
        Locator multicastDestination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        Func<ParticipantData> participantDataProvider,
        TimeSpan interval,
        ILogger? logger = null)
    {
        _transport = transport;
        _multicastDestination = multicastDestination;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _participantDataProvider = participantDataProvider;
        _interval = interval;
        _logger = logger ?? NullLogger.Instance;
        _sequenceNumber = 0;
    }

    /// <summary>送信ループを起動する。最初の送信は即座に行う。</summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_loopTask is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => SendLoopAsync(token), token);
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }
        _cts.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 想定内
        }
        catch (Exception ex)
        {
            _logger.Warn("SpdpWriter send loop did not exit cleanly", ex);
        }
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    public async Task SendOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var data = _participantDataProvider();
        long sn = Interlocked.Increment(ref _sequenceNumber);
        var packet = BuildSpdpMessage(data, sn);
        try
        {
            await _transport.SendAsync(packet, _multicastDestination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("SpdpWriter SendAsync failed", ex);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        // 起動直後に 1 回送ってから周期に入る
        await SendOnceAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SendOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>ParticipantData から RTPS メッセージのバイト列を組み立てる。</summary>
    public ReadOnlyMemory<byte> BuildSpdpMessage(ParticipantData data, long sequenceNumber)
    {
        // 1) SerializedPayload (encap 4B + PL_CDR) を組み立てる
        var payloadBuf = ArrayPool<byte>.Shared.Rent(1024);
        int payloadLength;
        try
        {
            CdrEncapsulation.Write(payloadBuf, CdrEncapsulation.PlCdrLittleEndian);
            var inner = new CdrWriter(payloadBuf, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
            ParticipantDataSerializer.Write(ref inner, data);
            payloadLength = inner.Position;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(payloadBuf);
            throw;
        }

        var payload = new byte[payloadLength];
        Buffer.BlockCopy(payloadBuf, 0, payload, 0, payloadLength);
        ArrayPool<byte>.Shared.Return(payloadBuf);

        // 2) RTPS メッセージを組み立てる
        var messageBuf = new byte[SendBufferSize];
        var writer = new RtpsMessageWriter(messageBuf, _version, _vendorId, _localPrefix);

        // INFO_TS (現在時刻)
        writer.WriteInfoTimestamp(new InfoTimestampSubmessage(Time.Now()));

        // DATA: SPDP_BUILTIN_PARTICIPANT_WRITER → unknown reader, with PL_CDR payload
        var dataSubmsg = new DataSubmessage(
            readerEntityId: EntityId.Unknown,
            writerEntityId: BuiltinEntityIds.SpdpBuiltinParticipantWriter,
            writerSn: new SequenceNumber(sequenceNumber),
            serializedPayload: payload,
            dataPresent: true);
        writer.WriteData(dataSubmsg);

        var result = new byte[writer.BytesWritten];
        writer.WrittenSpan.CopyTo(result);
        return result;
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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
