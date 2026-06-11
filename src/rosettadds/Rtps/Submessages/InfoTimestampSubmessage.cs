using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// INFO_TS Submessage。RTPS 仕様 9.4.5.6。
/// 後続 DATA submessage に適用するソース時刻を伝える。
/// I フラグが set の場合、以降の DATA に時刻情報を付加しない (本体なし)。
/// </summary>
public readonly struct InfoTimestampSubmessage
{
    /// <summary>I フラグ。true の場合 Timestamp は無効 (body 0 バイト)。</summary>
    public bool Invalidate { get; }

    /// <summary>Invalidate=false の場合の時刻。</summary>
    public Time Timestamp { get; }

    public InfoTimestampSubmessage(Time timestamp)
    {
        Invalidate = false;
        Timestamp = timestamp;
    }

    public static InfoTimestampSubmessage CreateInvalidate() => new(invalidate: true);

    private InfoTimestampSubmessage(bool invalidate)
    {
        Invalidate = invalidate;
        Timestamp = default;
    }

    /// <summary>このフィールドから生成される flags の追加ビット (Endianness は除く)。</summary>
    public byte ExtraFlags => Invalidate ? SubmessageFlags.InfoTsInvalidate : (byte)0;

    /// <summary>本体バイト数 (Invalidate=true なら 0、false なら 8)。</summary>
    public int BodySize => Invalidate ? 0 : Time.Size;

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        if (Invalidate)
        {
            return;
        }
        Timestamp.WriteTo(destination, endianness == CdrEndianness.LittleEndian);
    }

    public static InfoTimestampSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        bool invalidate = (flags & SubmessageFlags.InfoTsInvalidate) != 0;
        if (invalidate)
        {
            return CreateInvalidate();
        }
        var time = Time.Read(body, endianness == CdrEndianness.LittleEndian);
        return new InfoTimestampSubmessage(time);
    }
}
