using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// GAP Submessage。RTPS 仕様 9.4.5.3。
/// Writer から Reader へ「以下のシーケンス番号は二度と送らない (関連性なし)」を伝える。
/// レイアウト: readerEntityId(4) + writerEntityId(4) + gapStart(8) + gapList(可変)
/// </summary>
public sealed class GapSubmessage
{
    public EntityId ReaderEntityId { get; }
    public EntityId WriterEntityId { get; }

    /// <summary>gapStart 以降、gapList.bitmapBase 未満のシーケンスは無関連と宣言される。</summary>
    public SequenceNumber GapStart { get; }

    /// <summary>gapList の bitmap で 1 が立つシーケンスも無関連 (= reader が無視してよい)。</summary>
    public SequenceNumberSet GapList { get; }

    public GapSubmessage(
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumber gapStart,
        SequenceNumberSet gapList)
    {
        ReaderEntityId = readerEntityId;
        WriterEntityId = writerEntityId;
        GapStart = gapStart;
        GapList = gapList ?? throw new ArgumentNullException(nameof(gapList));
    }

    /// <summary>追加 flags ビットなし (Endianness のみ)。</summary>
    public byte ExtraFlags => 0;

    public int BodySize => 4 + 4 + 8 + GapList.SerializedSize;

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
        GapStart.WriteTo(destination.Slice(8, 8), littleEndian);
        GapList.WriteTo(destination.Slice(16, GapList.SerializedSize), littleEndian);
    }

    public static GapSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        _ = flags;
        if (body.Length < 16)
        {
            throw new ArgumentException("Body too small for GAP header.", nameof(body));
        }
        var readerId = EntityId.Read(body[..4]);
        var writerId = EntityId.Read(body.Slice(4, 4));
        bool littleEndian = endianness == CdrEndianness.LittleEndian;
        var gapStart = SequenceNumber.Read(body.Slice(8, 8), littleEndian);
        var gapList = SequenceNumberSet.Read(body[16..], littleEndian, out _);
        return new GapSubmessage(readerId, writerId, gapStart, gapList);
    }
}
