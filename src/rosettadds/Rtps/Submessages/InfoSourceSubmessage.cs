using ROSettaDDS.Cdr;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// INFO_SRC Submessage。RTPS 仕様 9.4.5.10。
/// 後続 submessage の送信元 (ProtocolVersion / VendorId / GuidPrefix) を上書きする。
/// Body レイアウト: unused(4B) + ProtocolVersion(2B) + VendorId(2B) + GuidPrefix(12B) = 20B。
/// </summary>
public readonly struct InfoSourceSubmessage
{
    public const int BodySize = 4 + 2 + 2 + GuidPrefix.Size;

    public ProtocolVersion Version { get; }
    public VendorId VendorId { get; }
    public GuidPrefix GuidPrefix { get; }

    public InfoSourceSubmessage(ProtocolVersion version, VendorId vendorId, GuidPrefix guidPrefix)
    {
        Version = version;
        VendorId = vendorId;
        GuidPrefix = guidPrefix;
    }

    public static InfoSourceSubmessage ReadBody(
        ReadOnlySpan<byte> body, CdrEndianness endianness, byte flags)
    {
        _ = endianness;
        _ = flags;
        // body[0..4] は unused
        var version = new ProtocolVersion(body[4], body[5]);
        var vendorId = new VendorId(body[6], body[7]);
        var guidPrefix = new GuidPrefix(body.Slice(8, GuidPrefix.Size));
        return new InfoSourceSubmessage(version, vendorId, guidPrefix);
    }
}
