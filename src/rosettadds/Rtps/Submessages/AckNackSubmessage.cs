using System.Buffers.Binary;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// ACKNACK Submessage。RTPS 仕様 9.4.5.2。
/// Reader から Writer へ「以下のシーケンスは未受信」を通知する。
/// 内部に <see cref="SequenceNumberSet"/> の bitmap を持つ。
/// レイアウト: readerEntityId(4) + writerEntityId(4) + readerSNState(可変) + count(4)
/// </summary>
public sealed class AckNackSubmessage
{
    /// <summary>F (Final): 1=応答不要 (preemptive ACKNACK 等)。</summary>
    public bool Final { get; }

    public EntityId ReaderEntityId { get; }
    public EntityId WriterEntityId { get; }
    public SequenceNumberSet ReaderSnState { get; }
    public int Count { get; }

    public AckNackSubmessage(
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumberSet readerSnState,
        int count,
        bool final = false)
    {
        ReaderEntityId = readerEntityId;
        WriterEntityId = writerEntityId;
        ReaderSnState = readerSnState ?? throw new ArgumentNullException(nameof(readerSnState));
        Count = count;
        Final = final;
    }

    public byte ExtraFlags => Final ? SubmessageFlags.AckNackFinal : (byte)0;

    public int BodySize => 4 + 4 + ReaderSnState.SerializedSize + 4;

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        if (destination.Length < BodySize)
        {
            throw new ArgumentException(
                $"Destination requires at least {BodySize} bytes.", nameof(destination));
        }
        ReaderEntityId.WriteTo(destination[..4]);
        WriterEntityId.WriteTo(destination.Slice(4, 4));
        bool littleEndian = endianness == CdrEndianness.LittleEndian;
        ReaderSnState.WriteTo(destination.Slice(8, ReaderSnState.SerializedSize), littleEndian);
        int countOffset = 8 + ReaderSnState.SerializedSize;
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(countOffset, 4), Count);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(countOffset, 4), Count);
        }
    }

    public static AckNackSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        if (body.Length < 8)
        {
            throw new ArgumentException("Body too small for ACKNACK header.", nameof(body));
        }
        var readerId = EntityId.Read(body[..4]);
        var writerId = EntityId.Read(body.Slice(4, 4));
        bool littleEndian = endianness == CdrEndianness.LittleEndian;
        var snState = SequenceNumberSet.Read(body[8..], littleEndian, out int snStateBytes);
        int countOffset = 8 + snStateBytes;
        if (body.Length < countOffset + 4)
        {
            throw new ArgumentException("Body too small for count after SequenceNumberSet.", nameof(body));
        }
        int count = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(body.Slice(countOffset, 4))
            : BinaryPrimitives.ReadInt32BigEndian(body.Slice(countOffset, 4));
        bool final = (flags & SubmessageFlags.AckNackFinal) != 0;
        return new AckNackSubmessage(readerId, writerId, snState, count, final);
    }
}
