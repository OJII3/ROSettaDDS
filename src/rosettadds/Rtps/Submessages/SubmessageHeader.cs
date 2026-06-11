using System.Buffers.Binary;
using ROSettaDDS.Cdr;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// RTPS Submessage ヘッダ (4 バイト)。RTPS 仕様 9.4.5.1。
/// レイアウト: kind (1B) + flags (1B) + length (uint16, flags の endian で読む)
/// length=0 はメッセージ末尾までを示す (最終 Submessage のみ許可)。
/// </summary>
public readonly struct SubmessageHeader
{
    public const int Size = 4;

    public SubmessageKind Kind { get; }
    public byte Flags { get; }
    public ushort Length { get; }

    public SubmessageHeader(SubmessageKind kind, byte flags, ushort length)
    {
        Kind = kind;
        Flags = flags;
        Length = length;
    }

    public bool IsLittleEndian => (Flags & SubmessageFlags.Endianness) != 0;

    public CdrEndianness Endianness => IsLittleEndian
        ? CdrEndianness.LittleEndian
        : CdrEndianness.BigEndian;

    /// <summary>length=0 は「メッセージ末尾まで」を意味する。最終 Submessage のみ許可。</summary>
    public bool IsLengthExtendedToEnd => Length == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        destination[0] = (byte)Kind;
        destination[1] = Flags;
        if (IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Length);
        }
    }

    public static SubmessageHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        var kind = (SubmessageKind)source[0];
        byte flags = source[1];
        bool littleEndian = (flags & SubmessageFlags.Endianness) != 0;
        ushort length = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(source[2..])
            : BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        return new SubmessageHeader(kind, flags, length);
    }
}
