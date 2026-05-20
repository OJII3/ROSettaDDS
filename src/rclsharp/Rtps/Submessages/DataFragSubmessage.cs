using System.Buffers.Binary;
using Rclsharp.Cdr;
using Rclsharp.Common;

namespace Rclsharp.Rtps.Submessages;

/// <summary>
/// DATA_FRAG Submessage。RTPS 仕様 9.4.5.3。
/// 大きな SerializedPayload を fragment 単位で運ぶ。
/// </summary>
public sealed class DataFragSubmessage
{
    /// <summary>標準レイアウト時の octetsToInlineQos の値 (28)。</summary>
    public const ushort StandardOctetsToInlineQos = 28;

    /// <summary>
    /// 固定ヘッダ部分のサイズ。
    /// extraFlags 2 + octetsToInlineQos 2 + readerId 4 + writerId 4 + writerSN 8
    /// + fragmentStartingNum 4 + fragmentsInSubmessage 2 + fragmentSize 2 + sampleSize 4 = 32。
    /// </summary>
    public const int FixedHeaderSize = 2 + 2 + 4 + 4 + 8 + 4 + 2 + 2 + 4;

    public bool InlineQosPresent { get; }
    public bool KeyPresent { get; }
    public bool NonStandardPayload { get; }

    public ushort ExtraFlagsValue { get; }
    public EntityId ReaderEntityId { get; }
    public EntityId WriterEntityId { get; }
    public SequenceNumber WriterSequenceNumber { get; }
    public uint FragmentStartingNumber { get; }
    public ushort FragmentsInSubmessage { get; }
    public ushort FragmentSize { get; }
    public uint SampleSize { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public ReadOnlyMemory<byte> SerializedPayloadFragment { get; }

    public DataFragSubmessage(
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumber writerSn,
        uint fragmentStartingNumber,
        ushort fragmentsInSubmessage,
        ushort fragmentSize,
        uint sampleSize,
        ReadOnlyMemory<byte> serializedPayloadFragment,
        ReadOnlyMemory<byte> inlineQos = default,
        bool keyPresent = false,
        bool nonStandardPayload = false,
        ushort extraFlags = 0)
    {
        if (fragmentStartingNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentStartingNumber), "Fragment numbers are 1-based.");
        }
        if (fragmentsInSubmessage == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentsInSubmessage), "At least one fragment is required.");
        }
        if (fragmentSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentSize), "Fragment size must be greater than zero.");
        }
        if (sampleSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), "Sample size must be greater than zero.");
        }

        ReaderEntityId = readerEntityId;
        WriterEntityId = writerEntityId;
        WriterSequenceNumber = writerSn;
        FragmentStartingNumber = fragmentStartingNumber;
        FragmentsInSubmessage = fragmentsInSubmessage;
        FragmentSize = fragmentSize;
        SampleSize = sampleSize;
        SerializedPayloadFragment = serializedPayloadFragment;
        InlineQos = inlineQos;
        InlineQosPresent = !inlineQos.IsEmpty;
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
            if (KeyPresent) f |= SubmessageFlags.DataKey;
            if (NonStandardPayload) f |= SubmessageFlags.DataNonStandardPayload;
            return f;
        }
    }

    public int BodySize => FixedHeaderSize + InlineQos.Length + SerializedPayloadFragment.Length;

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        if (destination.Length < BodySize)
        {
            throw new ArgumentException(
                $"Destination requires at least {BodySize} bytes.", nameof(destination));
        }
        bool littleEndian = endianness == CdrEndianness.LittleEndian;

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

        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(20, 4), FragmentStartingNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(24, 2), FragmentsInSubmessage);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(26, 2), FragmentSize);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), SampleSize);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(20, 4), FragmentStartingNumber);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(24, 2), FragmentsInSubmessage);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(26, 2), FragmentSize);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(28, 4), SampleSize);
        }

        int offset = FixedHeaderSize;
        if (!InlineQos.IsEmpty)
        {
            InlineQos.Span.CopyTo(destination[offset..]);
            offset += InlineQos.Length;
        }
        if (!SerializedPayloadFragment.IsEmpty)
        {
            SerializedPayloadFragment.Span.CopyTo(destination[offset..]);
        }
    }

    public static DataFragSubmessage ReadBody(
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
        uint fragmentStartingNum = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(20, 4));
        ushort fragmentsInSubmessage = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(24, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(body.Slice(24, 2));
        ushort fragmentSize = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(26, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(body.Slice(26, 2));
        uint sampleSize = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(28, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(28, 4));

        int inlineQosStart = 4 + octetsToInlineQos;
        if (inlineQosStart > body.Length)
        {
            throw new InvalidDataException(
                $"octetsToInlineQos={octetsToInlineQos} points past body end ({body.Length}).");
        }

        bool inlineQosPresent = (flags & SubmessageFlags.DataInlineQos) != 0;
        bool keyPresent = (flags & SubmessageFlags.DataKey) != 0;
        bool nonStandardPayload = (flags & SubmessageFlags.DataNonStandardPayload) != 0;

        int payloadStart = inlineQosStart;
        ReadOnlyMemory<byte> inlineQos = default;
        if (inlineQosPresent)
        {
            int inlineQosLength = DataSubmessage.ScanParameterListLength(body[inlineQosStart..], littleEndian);
            inlineQos = body.Slice(inlineQosStart, inlineQosLength).ToArray();
            payloadStart += inlineQosLength;
        }

        ReadOnlyMemory<byte> serializedPayloadFragment = default;
        if (payloadStart < body.Length)
        {
            serializedPayloadFragment = body[payloadStart..].ToArray();
        }

        return new DataFragSubmessage(
            readerId,
            writerId,
            writerSn,
            fragmentStartingNum,
            fragmentsInSubmessage,
            fragmentSize,
            sampleSize,
            serializedPayloadFragment,
            inlineQos,
            keyPresent,
            nonStandardPayload,
            extraFlagsValue);
    }
}
