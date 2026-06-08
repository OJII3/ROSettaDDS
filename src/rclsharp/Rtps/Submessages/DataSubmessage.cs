using System.Buffers.Binary;
using Rclsharp.Cdr;
using Rclsharp.Cdr.ParameterList;
using Rclsharp.Common;

namespace Rclsharp.Rtps.Submessages;

/// <summary>
/// DATA Submessage。RTPS 仕様 9.4.5.3。
/// レイアウト:
/// - extraFlags (uint16, 通常 0)
/// - octetsToInlineQos (uint16): octetsToInlineQos フィールド末尾から InlineQos 開始までのオクテット数。標準レイアウトでは 16
/// - readerEntityId (4B), writerEntityId (4B), writerSN (8B)
/// - inlineQos (ParameterList): Q フラグが set のとき
/// - serializedPayload (可変): D または K フラグが set のとき。submessage 末尾まで
///
/// <para>
/// SerializedPayload には CDR エンキャプスレーションヘッダ (4B) を含む。
/// InlineQos は PL_CDR (SENTINEL 込み) を含む。
/// </para>
/// </summary>
public sealed class DataSubmessage
{
    public const uint StatusInfoDisposed = 0x00000001u;
    public const uint StatusInfoUnregistered = 0x00000002u;
    public const uint StatusInfoFiltered = 0x00000004u;

    /// <summary>標準レイアウト時の octetsToInlineQos の値 (16)。</summary>
    public const ushort StandardOctetsToInlineQos = 16;

    /// <summary>固定ヘッダ部分のサイズ (extraFlags 2 + octetsToInlineQos 2 + readerId 4 + writerId 4 + writerSN 8 = 20)。</summary>
    public const int FixedHeaderSize = 2 + 2 + 4 + 4 + 8;

    public bool InlineQosPresent { get; }
    public bool DataPresent { get; }
    public bool KeyPresent { get; }
    public bool NonStandardPayload { get; }

    public ushort ExtraFlagsValue { get; }
    public EntityId ReaderEntityId { get; }
    public EntityId WriterEntityId { get; }
    public SequenceNumber WriterSequenceNumber { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public ReadOnlyMemory<byte> SerializedPayload { get; }

    public DataSubmessage(
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumber writerSn,
        ReadOnlyMemory<byte> serializedPayload = default,
        ReadOnlyMemory<byte> inlineQos = default,
        bool dataPresent = true,
        bool keyPresent = false,
        bool nonStandardPayload = false,
        ushort extraFlags = 0)
    {
        ReaderEntityId = readerEntityId;
        WriterEntityId = writerEntityId;
        WriterSequenceNumber = writerSn;
        SerializedPayload = serializedPayload;
        InlineQos = inlineQos;
        InlineQosPresent = !inlineQos.IsEmpty;
        DataPresent = dataPresent;
        KeyPresent = keyPresent;
        NonStandardPayload = nonStandardPayload;
        ExtraFlagsValue = extraFlags;
    }

    public byte ExtraFlags
    {
        get
        {
            byte f = 0;
            if (InlineQosPresent) f |= SubmessageFlags.DataInlineQos;
            if (DataPresent) f |= SubmessageFlags.DataData;
            if (KeyPresent) f |= SubmessageFlags.DataKey;
            if (NonStandardPayload) f |= SubmessageFlags.DataNonStandardPayload;
            return f;
        }
    }

    public int BodySize => FixedHeaderSize + InlineQos.Length + SerializedPayload.Length;

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        if (destination.Length < BodySize)
        {
            throw new ArgumentException(
                $"Destination requires at least {BodySize} bytes.", nameof(destination));
        }
        bool littleEndian = endianness == CdrEndianness.LittleEndian;

        // extraFlags
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[..2], ExtraFlagsValue);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2, 2), StandardOctetsToInlineQos);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination[..2], ExtraFlagsValue);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), StandardOctetsToInlineQos);
        }

        ReaderEntityId.WriteTo(destination.Slice(4, 4));
        WriterEntityId.WriteTo(destination.Slice(8, 4));
        WriterSequenceNumber.WriteTo(destination.Slice(12, 8), littleEndian);

        int offset = FixedHeaderSize;
        if (!InlineQos.IsEmpty)
        {
            InlineQos.Span.CopyTo(destination[offset..]);
            offset += InlineQos.Length;
        }
        if (!SerializedPayload.IsEmpty)
        {
            SerializedPayload.Span.CopyTo(destination[offset..]);
        }
    }

    public static byte[] BuildStatusInfoInlineQos(uint statusInfo, CdrEndianness endianness)
    {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new CdrWriter(buffer, endianness);
        var pl = new ParameterListWriter(writer);
        pl.BeginParameter(ParameterId.StatusInfo);
        pl.WriteUInt32(statusInfo);
        pl.EndParameter();
        pl.WriteSentinel();

        var current = pl.CurrentWriter;
        var copy = new byte[current.Position];
        current.WrittenSpan.CopyTo(copy);
        return copy;
    }

    public static bool TryReadStatusInfo(
        ReadOnlySpan<byte> inlineQos,
        CdrEndianness endianness,
        out uint statusInfo)
    {
        statusInfo = 0;
        if (inlineQos.IsEmpty)
        {
            return false;
        }

        var reader = new CdrReader(inlineQos, endianness);
        var pl = new ParameterListReader(reader);
        while (pl.MoveNext(out var pid, out _))
        {
            if (ParameterId.StripFlags(pid) != ParameterId.StatusInfo)
            {
                continue;
            }
            var raw = pl.CurrentValueRaw();
            if (raw.Length < sizeof(uint))
            {
                return false;
            }
            statusInfo = endianness == CdrEndianness.LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(raw)
                : BinaryPrimitives.ReadUInt32BigEndian(raw);
            return true;
        }
        return false;
    }

    /// <summary>
    /// DATA の本体を読み出す。submessage の length=0 (= 末尾まで) の場合、body は呼び出し側で
    /// 既にメッセージ末尾までスライスされている前提。
    /// InlineQos / SerializedPayload は新しい byte[] にコピーして返す。
    /// </summary>
    public static DataSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        if (body.Length < FixedHeaderSize)
        {
            throw new ArgumentException(
                $"Body requires at least {FixedHeaderSize} bytes.", nameof(body));
        }
        bool littleEndian = endianness == CdrEndianness.LittleEndian;

        ushort extraFlagsValue = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body[..2])
            : BinaryPrimitives.ReadUInt16BigEndian(body[..2]);
        ushort octetsToInlineQos = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2, 2));

        var readerId = EntityId.Read(body.Slice(4, 4));
        var writerId = EntityId.Read(body.Slice(8, 4));
        var writerSn = SequenceNumber.Read(body.Slice(12, 8), littleEndian);

        // octetsToInlineQos は octetsToInlineQos フィールド末尾 (offset 4) から InlineQos 開始までのオフセット
        int inlineQosStart = 4 + octetsToInlineQos;
        if (inlineQosStart > body.Length)
        {
            throw new InvalidDataException(
                $"octetsToInlineQos={octetsToInlineQos} points past body end ({body.Length}).");
        }

        bool inlineQosPresent = (flags & SubmessageFlags.DataInlineQos) != 0;
        bool dataPresent = (flags & SubmessageFlags.DataData) != 0;
        bool keyPresent = (flags & SubmessageFlags.DataKey) != 0;
        bool nonStandardPayload = (flags & SubmessageFlags.DataNonStandardPayload) != 0;

        int payloadStart = inlineQosStart;
        ReadOnlyMemory<byte> inlineQos = default;
        if (inlineQosPresent)
        {
            int inlineQosLength = ScanParameterListLength(body[inlineQosStart..], littleEndian);
            inlineQos = body.Slice(inlineQosStart, inlineQosLength).ToArray();
            payloadStart += inlineQosLength;
        }

        ReadOnlyMemory<byte> serializedPayload = default;
        if ((dataPresent || keyPresent) && payloadStart < body.Length)
        {
            int payloadLength = body.Length - payloadStart;
            serializedPayload = body.Slice(payloadStart, payloadLength).ToArray();
        }

        return new DataSubmessage(
            readerId, writerId, writerSn,
            serializedPayload, inlineQos,
            dataPresent, keyPresent, nonStandardPayload, extraFlagsValue);
    }

    /// <summary>
    /// DATA の本体を読み出し、InlineQos / SerializedPayload は入力 memory の slice として返す。
    /// 返却された memory は呼び出し元が保持する packet buffer の寿命内だけ有効。
    /// </summary>
    public static DataSubmessage ReadBodyBorrowed(
        ReadOnlyMemory<byte> bodyMemory, CdrEndianness endianness, byte flags)
    {
        var body = bodyMemory.Span;
        if (body.Length < FixedHeaderSize)
        {
            throw new ArgumentException(
                $"Body requires at least {FixedHeaderSize} bytes.", nameof(bodyMemory));
        }
        bool littleEndian = endianness == CdrEndianness.LittleEndian;

        ushort extraFlagsValue = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body[..2])
            : BinaryPrimitives.ReadUInt16BigEndian(body[..2]);
        ushort octetsToInlineQos = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2, 2));

        var readerId = EntityId.Read(body.Slice(4, 4));
        var writerId = EntityId.Read(body.Slice(8, 4));
        var writerSn = SequenceNumber.Read(body.Slice(12, 8), littleEndian);

        int inlineQosStart = 4 + octetsToInlineQos;
        if (inlineQosStart > body.Length)
        {
            throw new InvalidDataException(
                $"octetsToInlineQos={octetsToInlineQos} points past body end ({body.Length}).");
        }

        bool inlineQosPresent = (flags & SubmessageFlags.DataInlineQos) != 0;
        bool dataPresent = (flags & SubmessageFlags.DataData) != 0;
        bool keyPresent = (flags & SubmessageFlags.DataKey) != 0;
        bool nonStandardPayload = (flags & SubmessageFlags.DataNonStandardPayload) != 0;

        int payloadStart = inlineQosStart;
        ReadOnlyMemory<byte> inlineQos = default;
        if (inlineQosPresent)
        {
            int inlineQosLength = ScanParameterListLength(body[inlineQosStart..], littleEndian);
            inlineQos = bodyMemory.Slice(inlineQosStart, inlineQosLength);
            payloadStart += inlineQosLength;
        }

        ReadOnlyMemory<byte> serializedPayload = default;
        if ((dataPresent || keyPresent) && payloadStart < body.Length)
        {
            int payloadLength = body.Length - payloadStart;
            serializedPayload = bodyMemory.Slice(payloadStart, payloadLength);
        }

        return new DataSubmessage(
            readerId, writerId, writerSn,
            serializedPayload, inlineQos,
            dataPresent, keyPresent, nonStandardPayload, extraFlagsValue);
    }

    /// <summary>
    /// PL_CDR ParameterList の終端 (SENTINEL) までを走査して長さを返す。SENTINEL を含む長さ。
    /// </summary>
    internal static int ScanParameterListLength(ReadOnlySpan<byte> source, bool littleEndian)
    {
        int offset = 0;
        while (offset + 4 <= source.Length)
        {
            ushort pid = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(source[offset..])
                : BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
            ushort len = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + 2)..])
                : BinaryPrimitives.ReadUInt16BigEndian(source[(offset + 2)..]);
            offset += 4 + len;
            if (pid == ParameterId.Sentinel)
            {
                return offset;
            }
        }
        throw new InvalidDataException("InlineQos ParameterList missing SENTINEL terminator.");
    }
}
