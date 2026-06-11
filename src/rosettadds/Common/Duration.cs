using System.Buffers.Binary;

namespace ROSettaDDS.Common;

/// <summary>
/// RTPS Duration_t (8 バイト)。RTPS 仕様 9.3.2。
/// 秒 (int32) + 端数 (uint32, 単位 2^-32 秒)。
/// QoS の Lease Duration、HEARTBEAT 周期等で使用。
/// </summary>
public readonly struct Duration : IEquatable<Duration>, IComparable<Duration>
{
    public const int Size = 8;
    private const double FractionPerSecond = 4_294_967_296.0; // 2^32

    public int Seconds { get; }
    public uint Fraction { get; }

    public Duration(int seconds, uint fraction)
    {
        Seconds = seconds;
        Fraction = fraction;
    }

    /// <summary>DURATION_ZERO (0, 0)。</summary>
    public static readonly Duration Zero = new(0, 0u);

    /// <summary>DURATION_INFINITE (0x7FFFFFFF, 0xFFFFFFFF)。</summary>
    public static readonly Duration Infinite = new(0x7FFF_FFFF, 0xFFFF_FFFFu);

    public static Duration FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        int s = (int)Math.Floor(seconds);
        double frac = seconds - s;
        uint fraction = (uint)(frac * FractionPerSecond);
        return new Duration(s, fraction);
    }

    public static Duration FromTimeSpan(TimeSpan ts) => FromSeconds(ts.TotalSeconds);

    public TimeSpan ToTimeSpan()
    {
        double seconds = Seconds + Fraction / FractionPerSecond;
        return TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
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

    public static Duration Read(ReadOnlySpan<byte> source, bool littleEndian)
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
        return new Duration(seconds, fraction);
    }

    public bool Equals(Duration other) => Seconds == other.Seconds && Fraction == other.Fraction;

    public int CompareTo(Duration other)
    {
        int cmp = Seconds.CompareTo(other.Seconds);
        return cmp != 0 ? cmp : Fraction.CompareTo(other.Fraction);
    }

    public override bool Equals(object? obj) => obj is Duration d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(Seconds, Fraction);
    public override string ToString() => $"{Seconds}.{Fraction:D10}";

    public static bool operator ==(Duration left, Duration right) => left.Equals(right);
    public static bool operator !=(Duration left, Duration right) => !left.Equals(right);
}
