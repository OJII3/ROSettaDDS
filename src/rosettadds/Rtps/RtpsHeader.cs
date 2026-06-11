using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps;

/// <summary>
/// RTPS Message ヘッダ (20 バイト)。RTPS 仕様 8.3.3.1 / 9.4.5.1。
/// レイアウト:
/// - protocol (4B): "RTPS" マジック
/// - version (2B): ProtocolVersion (major, minor)
/// - vendorId (2B): VendorId
/// - guidPrefix (12B): 送信元参加者の GuidPrefix
/// </summary>
public static class RtpsHeader
{
    public const int Size = 20;

    /// <summary>"RTPS" マジック (4 バイト固定)。</summary>
    private static readonly byte[] s_Magic = { (byte)'R', (byte)'T', (byte)'P', (byte)'S' };
    public static ReadOnlySpan<byte> Magic => s_Magic;

    /// <summary>ヘッダを書き込む。</summary>
    public static void Write(
        Span<byte> destination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix guidPrefix)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        Magic.CopyTo(destination);
        destination[4] = version.Major;
        destination[5] = version.Minor;
        destination[6] = vendorId.V0;
        destination[7] = vendorId.V1;
        guidPrefix.CopyTo(destination.Slice(8, GuidPrefix.Size));
    }

    /// <summary>
    /// ヘッダを読み出す。マジックが "RTPS" でなければ例外。
    /// </summary>
    public static (ProtocolVersion version, VendorId vendorId, GuidPrefix guidPrefix) Read(
        ReadOnlySpan<byte> source)
    {
        if (!TryRead(source, out var version, out var vendorId, out var guidPrefix))
        {
            throw new InvalidDataException("RTPS header magic mismatch (expected 'RTPS').");
        }
        return (version, vendorId, guidPrefix);
    }

    /// <summary>
    /// ヘッダを試行的に読み出す。マジック不一致や長さ不足時は false。
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> source,
        out ProtocolVersion version,
        out VendorId vendorId,
        out GuidPrefix guidPrefix)
    {
        version = default;
        vendorId = default;
        guidPrefix = default;
        if (source.Length < Size)
        {
            return false;
        }
        if (!source[..4].SequenceEqual(Magic))
        {
            return false;
        }
        version = new ProtocolVersion(source[4], source[5]);
        vendorId = new VendorId(source[6], source[7]);
        guidPrefix = new GuidPrefix(source.Slice(8, GuidPrefix.Size));
        return true;
    }
}
