using System.Buffers;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// StatefulWriter の packet 構築と送信を担当する内部クラス。
/// DATA/DATA_FRAG/HEARTBEAT/GAP の構築と IRtpsTransport への送信を封装する。
/// </summary>
internal sealed class StatefulWriterPacketSender
{
    public const int SendBufferSize = 1500;
    public const int DataFragPayloadSize = 1024;

    private readonly IRtpsTransport _transport;
    private readonly ProtocolVersion _version;
    private readonly VendorId _vendorId;
    private readonly GuidPrefix _localPrefix;
    private readonly EntityId _writerEntityId;
    private readonly ILogger _logger;

    public StatefulWriterPacketSender(
        IRtpsTransport transport,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId writerEntityId,
        ILogger logger)
    {
        _transport = transport;
        _version = version;
        _vendorId = vendorId;
        _localPrefix = localPrefix;
        _writerEntityId = writerEntityId;
        _logger = logger;
    }

    /// <summary>
    /// DATA メッセージを送信する。ペイロードサイズに応じて DATA_FRAG に分割する。
    /// </summary>
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
                int written = BuildDataPacket(change, readerEntityId, inlineQos, isAlive, scratch);
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

    /// <summary>
    /// HEARTBEAT メッセージを送信する。
    /// </summary>
    public async ValueTask SendHeartbeatAsync(
        SequenceNumber first,
        SequenceNumber last,
        EntityId readerEntityId,
        Locator destination,
        int count,
        CancellationToken cancellationToken)
    {
        var packet = BuildHeartbeatPacket(first, last, readerEntityId, count);
        if (packet.Length == 0) return;

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

    /// <summary>
    /// GAP メッセージを送信する。
    /// </summary>
    public async ValueTask SendGapAsync(
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

    private byte[] BuildHeartbeatPacket(SequenceNumber first, SequenceNumber last, EntityId readerEntityId, int count)
    {
        var hb = new HeartbeatSubmessage(
            readerEntityId, _writerEntityId, first, last, count, final: false, liveliness: false);

        var buffer = new byte[SendBufferSize];
        var msg = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
        msg.WriteHeartbeat(hb);
        var packet = new byte[msg.BytesWritten];
        msg.WrittenSpan.CopyTo(packet);
        return packet;
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

    private int BuildDataPacket(
        CacheChange change,
        EntityId readerEntityId,
        ReadOnlyMemory<byte> inlineQos,
        bool isAlive,
        byte[] destination)
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
        return writer.BytesWritten;
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
        if (change.SerializedPayload.Length == 0) return;

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

    private int WriteDataFragToScratch(byte[] buffer, DataFragSubmessage dataFrag)
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
}
