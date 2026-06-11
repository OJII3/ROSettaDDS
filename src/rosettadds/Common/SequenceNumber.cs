using System.Buffers.Binary;

namespace ROSettaDDS.Common;

/// <summary>
/// RTPS SequenceNumber (64 ビット符号付き)。RTPS 仕様 8.3.5.4。
/// ワイヤ上は (high: int32, low: uint32) の順で 8 バイト。
/// SEQUENCENUMBER_UNKNOWN は high=-1, low=0 で表現される (= -2^32)。
/// </summary>
public readonly struct SequenceNumber : IEquatable<SequenceNumber>, IComparable<SequenceNumber>
{
    public const int Size = 8;

    public long Value { get; }

    public SequenceNumber(long value)
    {
        Value = value;
    }

    public SequenceNumber(int high, uint low)
    {
        Value = ((long)high << 32) | low;
    }

    /// <summary>SEQUENCENUMBER_UNKNOWN (high=-1, low=0)。</summary>
    public static readonly SequenceNumber Unknown = new(-1, 0u);

    /// <summary>0 (シーケンス番号は 1 始まりなので無効値扱い)。</summary>
    public static readonly SequenceNumber Zero = new(0L);

    public int High => (int)(Value >> 32);
    public uint Low => (uint)(Value & 0xFFFF_FFFFu);

    public bool IsUnknown => Value == Unknown.Value;

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, High);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Low);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, High);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Low);
        }
    }

    public static SequenceNumber Read(ReadOnlySpan<byte> source, bool littleEndian)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        int high = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(source)
            : BinaryPrimitives.ReadInt32BigEndian(source);
        uint low = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return new SequenceNumber(high, low);
    }

    public bool Equals(SequenceNumber other) => Value == other.Value;
    public int CompareTo(SequenceNumber other) => Value.CompareTo(other.Value);
    public override bool Equals(object? obj) => obj is SequenceNumber s && Equals(s);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => IsUnknown ? "UNKNOWN" : Value.ToString();

    public static bool operator ==(SequenceNumber left, SequenceNumber right) => left.Equals(right);
    public static bool operator !=(SequenceNumber left, SequenceNumber right) => !left.Equals(right);
    public static bool operator <(SequenceNumber left, SequenceNumber right) => left.Value < right.Value;
    public static bool operator >(SequenceNumber left, SequenceNumber right) => left.Value > right.Value;
    public static bool operator <=(SequenceNumber left, SequenceNumber right) => left.Value <= right.Value;
    public static bool operator >=(SequenceNumber left, SequenceNumber right) => left.Value >= right.Value;

    public static SequenceNumber operator +(SequenceNumber left, long right) => new(left.Value + right);
    public static SequenceNumber operator -(SequenceNumber left, long right) => new(left.Value - right);
    public static long operator -(SequenceNumber left, SequenceNumber right) => left.Value - right.Value;
}
