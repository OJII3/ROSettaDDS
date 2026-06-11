using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// INFO_DST Submessage。RTPS 仕様 9.4.5.5。
/// 後続 submessage の宛先 Participant の GuidPrefix を指定する。
/// </summary>
public readonly struct InfoDestinationSubmessage
{
    public GuidPrefix GuidPrefix { get; }

    public InfoDestinationSubmessage(GuidPrefix guidPrefix)
    {
        GuidPrefix = guidPrefix;
    }

    /// <summary>追加 flags ビットなし。</summary>
    public byte ExtraFlags => 0;

    public int BodySize => GuidPrefix.Size;

    public void WriteBody(Span<byte> destination, CdrEndianness endianness)
    {
        // GuidPrefix は単なる 12 バイト、エンディアン非依存
        _ = endianness;
        GuidPrefix.CopyTo(destination);
    }

    public static InfoDestinationSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        _ = endianness;
        _ = flags;
        return new InfoDestinationSubmessage(new GuidPrefix(body[..GuidPrefix.Size]));
    }
}
