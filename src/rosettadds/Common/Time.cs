using System.Buffers.Binary;

namespace ROSettaDDS.Common;

/// <summary>
/// RTPS Time_t (8 バイト)。RTPS 仕様 9.3.2。
/// 秒 (int32) + 端数 (uint32, 単位 2^-32 秒) で表現。基準は Unix エポック。
/// </summary>
public readonly struct Time : IEquatable<Time>, IComparable<Time>
{
    public const int Size = 8;
    private const double FractionPerSecond = 4_294_967_296.0; // 2^32

    public int Seconds { get; }
    public uint Fraction { get; }

    public Time(int seconds, uint fraction)
    {
        Seconds = seconds;
        Fraction = fraction;
    }

    /// <summary>TIME_ZERO (0, 0)。</summary>
    public static readonly Time Zero = new(0, 0u);

    /// <summary>TIME_INVALID (-1, 0xFFFFFFFF)。</summary>
    public static readonly Time Invalid = new(-1, 0xFFFF_FFFFu);

    /// <summary>TIME_INFINITE (0x7FFFFFFF, 0xFFFFFFFF)。</summary>
    public static readonly Time Infinite = new(0x7FFF_FFFF, 0xFFFF_FFFFu);

    /// <summary>UTC 現在時刻から Time を生成。</summary>
    public static Time Now() => FromDateTime(DateTime.UtcNow);

    public static Time FromDateTime(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
        {
            utc = utc.ToUniversalTime();
        }
        var elapsed = utc - DateTime.UnixEpoch;
        long totalSeconds = (long)Math.Floor(elapsed.TotalSeconds);
        double frac = elapsed.TotalSeconds - totalSeconds;
        uint fraction = (uint)(frac * FractionPerSecond);
        return new Time((int)totalSeconds, fraction);
    }

    public DateTime ToDateTime()
    {
        double seconds = Seconds + Fraction / FractionPerSecond;
        return DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
    }

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, Seconds);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Fraction);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, Seconds);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Fraction);
        }
    }

    public static Time Read(ReadOnlySpan<byte> source, bool littleEndian)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        int seconds = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(source)
            : BinaryPrimitives.ReadInt32BigEndian(source);
        uint fraction = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(source[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        return new Time(seconds, fraction);
    }

    public bool Equals(Time other) => Seconds == other.Seconds && Fraction == other.Fraction;

    public int CompareTo(Time other)
    {
        int cmp = Seconds.CompareTo(other.Seconds);
        return cmp != 0 ? cmp : Fraction.CompareTo(other.Fraction);
    }

    public override bool Equals(object? obj) => obj is Time t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Seconds, Fraction);
    public override string ToString() => $"{Seconds}.{Fraction:D10}";

    public static bool operator ==(Time left, Time right) => left.Equals(right);
    public static bool operator !=(Time left, Time right) => !left.Equals(right);
}
