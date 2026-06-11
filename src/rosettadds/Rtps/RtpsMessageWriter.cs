using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;

namespace ROSettaDDS.Rtps;

/// <summary>
/// RTPS Message ライタ。<see cref="RtpsHeader"/> + 複数の Submessage を構築する。
/// 各 WriteXxx は SubmessageHeader (4B) + Body をまとめて書き込む。
/// </summary>
public ref struct RtpsMessageWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public RtpsMessageWriter(
        Span<byte> buffer,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix sourceGuidPrefix)
    {
        if (buffer.Length < RtpsHeader.Size)
        {
            throw new ArgumentException(
                $"Buffer requires at least {RtpsHeader.Size} bytes for RTPS header.", nameof(buffer));
        }
        _buffer = buffer;
        RtpsHeader.Write(_buffer, version, vendorId, sourceGuidPrefix);
        _position = RtpsHeader.Size;
    }

    /// <summary>これまで書き込んだバイト数 (= 書き込んだ全 message の長さ)。</summary>
    public int BytesWritten => _position;

    /// <summary>これまで書き込んだ範囲。</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    public void WriteInfoTimestamp(InfoTimestampSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.InfoTimestamp, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteInfoDestination(InfoDestinationSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.InfoDestination, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteHeartbeat(HeartbeatSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.Heartbeat, submessage.ExtraFlags, HeartbeatSubmessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteAckNack(AckNackSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.AckNack, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteGap(GapSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.Gap, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteData(DataSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.Data, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    public void WriteDataFrag(DataFragSubmessage submessage, CdrEndianness endianness = CdrEndianness.LittleEndian)
        => WriteSubmessage(SubmessageKind.DataFrag, submessage.ExtraFlags, submessage.BodySize, endianness,
            (b, e) => submessage.WriteBody(b, e));

    private delegate void BodyWriter(Span<byte> body, CdrEndianness endianness);

    private void WriteSubmessage(
        SubmessageKind kind,
        byte extraFlags,
        int bodySize,
        CdrEndianness endianness,
        BodyWriter writeBody)
    {
        if (bodySize < 0 || bodySize > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bodySize),
                $"Submessage body size must fit in uint16 (got {bodySize}).");
        }
        int totalSize = SubmessageHeader.Size + bodySize;
        if (_position + totalSize > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"RTPS message buffer overflow: needed {totalSize} bytes at position {_position} but capacity is {_buffer.Length}.");
        }
        byte flags = extraFlags;
        if (endianness == CdrEndianness.LittleEndian)
        {
            flags |= SubmessageFlags.Endianness;
        }
        var header = new SubmessageHeader(kind, flags, (ushort)bodySize);
        header.WriteTo(_buffer.Slice(_position, SubmessageHeader.Size));
        _position += SubmessageHeader.Size;
        writeBody(_buffer.Slice(_position, bodySize), endianness);
        _position += bodySize;
    }
}
