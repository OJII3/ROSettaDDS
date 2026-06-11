using System.Buffers.Binary;

namespace ROSettaDDS.Cdr;

/// <summary>
/// OMG CDR エンキャプスレーションヘッダ (4 バイト)。RTPS 仕様 10.5。
/// レイアウト: kind (uint16, BE 固定) + options (uint16, BE 固定)。
/// kind の下位 1 ビットがエンディアン (0=BE, 1=LE)、上位ビットが Plain CDR / PL_CDR を示す。
/// </summary>
public static class CdrEncapsulation
{
    public const int Size = 4;

    /// <summary>Plain CDR, Big Endian (0x0000)。</summary>
    public const ushort CdrBigEndian = 0x0000;

    /// <summary>Plain CDR, Little Endian (0x0001)。</summary>
    public const ushort CdrLittleEndian = 0x0001;

    /// <summary>Parameter List CDR, Big Endian (0x0002)。SPDP/SEDP で使用。</summary>
    public const ushort PlCdrBigEndian = 0x0002;

    /// <summary>Parameter List CDR, Little Endian (0x0003)。</summary>
    public const ushort PlCdrLittleEndian = 0x0003;

    /// <summary>Endianness をヘッダ値に変換。</summary>
    public static ushort PlainCdr(CdrEndianness endianness)
        => endianness == CdrEndianness.LittleEndian ? CdrLittleEndian : CdrBigEndian;

    /// <summary>PL_CDR ヘッダ値を返す。</summary>
    public static ushort ParameterListCdr(CdrEndianness endianness)
        => endianness == CdrEndianness.LittleEndian ? PlCdrLittleEndian : PlCdrBigEndian;

    /// <summary>kind 値からサポート済み CDR/PL_CDR の Endianness を判定する。</summary>
    public static CdrEndianness GetEndianness(ushort kind)
        => kind switch
        {
            CdrBigEndian or PlCdrBigEndian => CdrEndianness.BigEndian,
            CdrLittleEndian or PlCdrLittleEndian => CdrEndianness.LittleEndian,
            _ => throw new NotSupportedException($"Unsupported CDR encapsulation kind 0x{kind:X4}."),
        };

    /// <summary>kind 値がサポート済み CDR/PL_CDR かどうか。</summary>
    public static bool IsSupported(ushort kind)
        => kind is CdrBigEndian or CdrLittleEndian or PlCdrBigEndian or PlCdrLittleEndian;

    /// <summary>kind 値が Plain CDR かどうか。</summary>
    public static bool IsPlainCdr(ushort kind) => kind is CdrBigEndian or CdrLittleEndian;

    /// <summary>kind 値が Parameter List CDR かどうか。</summary>
    public static bool IsParameterList(ushort kind) => kind is PlCdrBigEndian or PlCdrLittleEndian;

    /// <summary>カプセルヘッダを書き込む。kind/options は常にビッグエンディアン。</summary>
    public static void Write(Span<byte> destination, ushort kind, ushort options = 0)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        BinaryPrimitives.WriteUInt16BigEndian(destination, kind);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], options);
    }

    /// <summary>カプセルヘッダを読み出す。</summary>
    public static (ushort kind, ushort options) Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        ushort kind = BinaryPrimitives.ReadUInt16BigEndian(source);
        ushort options = BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        return (kind, options);
    }
}
