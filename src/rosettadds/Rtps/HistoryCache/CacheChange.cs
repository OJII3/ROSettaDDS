using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// 1 サンプル (1 つの DATA submessage 相当) を表す不変オブジェクト。
/// RTPS 仕様 8.2.7 / 8.7.5 (CacheChange)。
/// </summary>
public sealed class CacheChange
{
    public ChangeKind Kind { get; }
    public Guid WriterGuid { get; }
    public SequenceNumber SequenceNumber { get; }
    public Time SourceTimestamp { get; }
    public ReadOnlyMemory<byte> SerializedPayload { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public CdrEndianness InlineQosEndianness { get; }
    /// <summary>
    /// Pool-owned ペイロードの所有者。所有権は通常 WriterHistoryCache が持ち、
    /// CacheChange 自身は dispose しません。所有者の release は WriterHistoryCache の
    /// Remove / RemoveBelowOrEqual / EvictIfNeeded / Dispose 経路で行います。
    /// </summary>
    internal RtpsPayloadOwner? PayloadOwner { get; }

    public CacheChange(
        ChangeKind kind,
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        Time sourceTimestamp,
        ReadOnlyMemory<byte> serializedPayload,
        ReadOnlyMemory<byte> inlineQos = default,
        CdrEndianness inlineQosEndianness = CdrEndianness.LittleEndian)
    {
        Kind = kind;
        WriterGuid = writerGuid;
        SequenceNumber = sequenceNumber;
        SourceTimestamp = sourceTimestamp;
        SerializedPayload = serializedPayload;
        InlineQos = inlineQos;
        InlineQosEndianness = inlineQosEndianness;
    }

    internal CacheChange(
        ChangeKind kind,
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        Time sourceTimestamp,
        ReadOnlyMemory<byte> serializedPayload,
        RtpsPayloadOwner? payloadOwner,
        ReadOnlyMemory<byte> inlineQos = default,
        CdrEndianness inlineQosEndianness = CdrEndianness.LittleEndian)
        : this(kind, writerGuid, sequenceNumber, sourceTimestamp, serializedPayload, inlineQos, inlineQosEndianness)
    {
        PayloadOwner = payloadOwner;
    }

    public override string ToString()
        => $"CacheChange({Kind}, writer={WriterGuid}, sn={SequenceNumber}, payload={SerializedPayload.Length}B)";
}
