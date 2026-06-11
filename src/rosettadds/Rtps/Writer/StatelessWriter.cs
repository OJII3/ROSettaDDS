using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// Stateless RTPS Writer。Best-Effort QoS 用。
/// Reader プロキシ等の状態を持たず、単純に DATA submessage を構築して指定 Locator へ送る。
///
/// <para>
/// 各 <see cref="WriteAsync"/> 呼び出しで writerSN を 1 つインクリメントし、
/// INFO_TS + DATA を含む RTPS message を送信する。
/// </para>
/// </summary>
public sealed class StatelessWriter
{
    /// <summary>送信バッファサイズ (1500 = MTU 想定)。フラグメンテーション非対応。</summary>
    public const int SendBufferSize = 1500;

    private readonly IRtpsTransport _transport;
    private readonly Locator _destination;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly EntityId _writerEntityId;
    private readonly ILogger _logger;

    private long _sequenceNumber;

    public Guid Guid { get; }
    public EntityId WriterEntityId => _writerEntityId;

    public StatelessWriter(
        IRtpsTransport transport,
        Locator destination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId writerEntityId,
        ILogger? logger = null)
    {
        _transport = transport;
        _destination = destination;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _writerEntityId = writerEntityId;
        _logger = logger ?? NullLogger.Instance;
        Guid = new Guid(localPrefix, writerEntityId);
    }

    /// <summary>シリアライズ済みペイロード (encap ヘッダ含む) を 1 回送信する。</summary>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> serializedPayload,
        CancellationToken cancellationToken = default)
    {
        long sn = Interlocked.Increment(ref _sequenceNumber);
        var packet = BuildPacket(serializedPayload, sn);
        try
        {
            await _transport.SendAsync(packet, _destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("StatelessWriter SendAsync failed", ex);
        }
    }

    /// <summary>RTPS message (header + INFO_TS + DATA) を組み立てる (テスト用に public)。</summary>
    public ReadOnlyMemory<byte> BuildPacket(ReadOnlyMemory<byte> serializedPayload, long sequenceNumber)
    {
        var buffer = new byte[SendBufferSize];
        var writer = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        writer.WriteInfoTimestamp(new InfoTimestampSubmessage(Time.Now()));
        var dataSubmsg = new DataSubmessage(
            readerEntityId: EntityId.Unknown,
            writerEntityId: _writerEntityId,
            writerSn: new SequenceNumber(sequenceNumber),
            serializedPayload: serializedPayload,
            dataPresent: true);
        writer.WriteData(dataSubmsg, CdrEndianness.LittleEndian);

        var result = new byte[writer.BytesWritten];
        writer.WrittenSpan.CopyTo(result);
        return result;
    }
}
