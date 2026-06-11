using System.Buffers.Binary;
using ROSettaDDS.Common;

namespace ROSettaDDS.Rtps.Submessages;

/// <summary>
/// RTPS SequenceNumberSet。RTPS 仕様 9.4.2.6 / Table 9.13。
/// レイアウト:
/// - bitmapBase (SequenceNumber, 8B)
/// - numBits (uint32, 4B)
/// - bitmap (uint32 × ceil(numBits/32))
///
/// <para>
/// 表現可能範囲は <c>[bitmapBase, bitmapBase + numBits)</c>。
/// 仕様上 numBits は 0〜256。bitmap[i] の bit j (= 0..31) が 1 のとき、
/// シーケンス番号 <c>bitmapBase + (i*32 + j)</c> が set されている。
/// </para>
/// </summary>
public sealed class SequenceNumberSet
{
    /// <summary>RTPS 仕様で許される numBits の最大値 (256)。</summary>
    public const int MaxNumBits = 256;

    public SequenceNumber BitmapBase { get; }
    public int NumBits { get; }
    public IReadOnlyList<uint> Bitmap { get; }

    public SequenceNumberSet(SequenceNumber bitmapBase, int numBits, IReadOnlyList<uint> bitmap)
    {
        if (numBits < 0 || numBits > MaxNumBits)
        {
            throw new ArgumentOutOfRangeException(nameof(numBits),
                $"numBits must be in [0, {MaxNumBits}]");
        }
        int requiredWords = (numBits + 31) / 32;
        if (bitmap.Count != requiredWords)
        {
            throw new ArgumentException(
                $"bitmap length must be {requiredWords} for numBits={numBits}, got {bitmap.Count}.",
                nameof(bitmap));
        }
        BitmapBase = bitmapBase;
        NumBits = numBits;
        Bitmap = bitmap;
    }

    /// <summary>シリアライズ後のサイズ (バイト)。</summary>
    public int SerializedSize => 8 + 4 + Bitmap.Count * 4;

    /// <summary>指定の bit が set されているか。</summary>
    public bool IsSet(int bitIndex)
    {
        if (bitIndex < 0 || bitIndex >= NumBits)
        {
            return false;
        }
        int word = bitIndex / 32;
        int bit = bitIndex % 32;
        // RTPS 仕様: 上位ビット (MSB = bit 31) が set 表現の bit 0
        // ※ 実装はベンダによって異なるが、Fast-DDS / CycloneDDS は MSB-first を採用
        return (Bitmap[word] & (1u << (31 - bit))) != 0;
    }

    /// <summary>set されている全シーケンス番号を返す。</summary>
    public IEnumerable<SequenceNumber> EnumerateSet()
    {
        for (int i = 0; i < NumBits; i++)
        {
            if (IsSet(i))
            {
                yield return BitmapBase + i;
            }
        }
    }

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < SerializedSize)
        {
            throw new ArgumentException(
                $"Destination requires at least {SerializedSize} bytes.", nameof(destination));
        }
        BitmapBase.WriteTo(destination, littleEndian);
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)NumBits);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[8..], (uint)NumBits);
        }
        int offset = 12;
        for (int i = 0; i < Bitmap.Count; i++)
        {
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], Bitmap[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], Bitmap[i]);
            }
            offset += 4;
        }
    }

    public static SequenceNumberSet Read(ReadOnlySpan<byte> source, bool littleEndian, out int bytesRead)
    {
        if (source.Length < 12)
        {
            throw new ArgumentException("Source too small to contain SequenceNumberSet header.", nameof(source));
        }
        var bitmapBase = SequenceNumber.Read(source[..8], littleEndian);
        uint numBits = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source[8..])
            : BinaryPrimitives.ReadUInt32BigEndian(source[8..]);
        if (numBits > MaxNumBits)
        {
            throw new InvalidDataException(
                $"SequenceNumberSet numBits={numBits} exceeds maximum {MaxNumBits}.");
        }
        int wordCount = ((int)numBits + 31) / 32;
        int totalSize = 12 + wordCount * 4;
        if (source.Length < totalSize)
        {
            throw new ArgumentException(
                $"Source too small: needed {totalSize} bytes for {wordCount}-word bitmap, got {source.Length}.",
                nameof(source));
        }
        var bitmap = new uint[wordCount];
        int offset = 12;
        for (int i = 0; i < wordCount; i++)
        {
            bitmap[i] = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(source[offset..])
                : BinaryPrimitives.ReadUInt32BigEndian(source[offset..]);
            offset += 4;
        }
        bytesRead = totalSize;
        return new SequenceNumberSet(bitmapBase, (int)numBits, bitmap);
    }
}
