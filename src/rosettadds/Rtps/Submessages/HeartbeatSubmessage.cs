using System.Buffers.Binary;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// HEARTBEAT Submessage。RTPS 仕様 9.4.5.7。
/// Reliable Writer から Reader へ「現在 [firstSN, lastSN] のシーケンスを持つ」と通知する。
/// 受信側はこれを基に欠損を検出して ACKNACK で要求する。
/// レイアウト: readerEntityId(4) + writerEntityId(4) + firstSN(8) + lastSN(8) + count(4) = 28B
/// </summary>
public readonly struct HeartbeatSubmessage
{
    public const int BodySize = 28;

    /// <summary>F (Final): 1=ACKNACK 応答不要。</summary>
    public bool Final { get; }

    /// <summary>L (Liveliness): 1=Liveliness 維持目的の HB。</summary>
    public bool Liveliness { get; }

    public EntityId ReaderEntityId { get; }
    public EntityId WriterEntityId { get; }
    public SequenceNumber FirstSequenceNumber { get; }
    public SequenceNumber LastSequenceNumber { get; }
    public int Count { get; }

    public HeartbeatSubmessage(
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumber firstSn,
        SequenceNumber lastSn,
        int count,
        bool final = false,
        bool liveliness = false)
    {
        ReaderEntityId = readerEntityId;
        WriterEntityId = writerEntityId;
        FirstSequenceNumber = firstSn;
        LastSequenceNumber = lastSn;
        Count = count;
        Final = final;
        Liveliness = liveliness;
    }

    public byte ExtraFlags
    {
        get
        {
            byte f = 0;
            if (Final) f |= SubmessageFlags.HeartbeatFinal;
            if (Liveliness) f |= SubmessageFlags.HeartbeatLiveliness;
            return f;
        }
    }

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        if (destination.Length < BodySize)
        {
            throw new ArgumentException(
                $"Destination requires at least {BodySize} bytes.", nameof(destination));
        }
        // EntityId は 4 オクテットで endian 非依存 (BE エンコード固定)
        ReaderEntityId.WriteTo(destination[..4]);
        WriterEntityId.WriteTo(destination.Slice(4, 4));

        bool littleEndian = endianness == CdrEndianness.LittleEndian;
        FirstSequenceNumber.WriteTo(destination.Slice(8, 8), littleEndian);
        LastSequenceNumber.WriteTo(destination.Slice(16, 8), littleEndian);
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), Count);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(24, 4), Count);
        }
    }

    public static HeartbeatSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        if (body.Length < BodySize)
        {
            throw new ArgumentException(
                $"Body requires at least {BodySize} bytes.", nameof(body));
        }
        var readerId = EntityId.Read(body[..4]);
        var writerId = EntityId.Read(body.Slice(4, 4));
        bool littleEndian = endianness == CdrEndianness.LittleEndian;
        var firstSn = SequenceNumber.Read(body.Slice(8, 8), littleEndian);
        var lastSn = SequenceNumber.Read(body.Slice(16, 8), littleEndian);
        int count = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(body.Slice(24, 4))
            : BinaryPrimitives.ReadInt32BigEndian(body.Slice(24, 4));
        bool final = (flags & SubmessageFlags.HeartbeatFinal) != 0;
        bool liveliness = (flags & SubmessageFlags.HeartbeatLiveliness) != 0;
        return new HeartbeatSubmessage(readerId, writerId, firstSn, lastSn, count, final, liveliness);
    }
}
