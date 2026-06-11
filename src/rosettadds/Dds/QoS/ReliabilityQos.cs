using ROSettaDDS.Common;

namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Reliability QoS Policy。kind + max_blocking_time。
/// PL_CDR 上では合計 12 バイト (kind 4B + Duration 8B)。
/// </summary>
public readonly struct ReliabilityQos : IEquatable<ReliabilityQos>
{
    public ReliabilityKind Kind { get; }
    public Duration MaxBlockingTime { get; }

    public ReliabilityQos(ReliabilityKind kind, Duration maxBlockingTime)
    {
        Kind = kind;
        MaxBlockingTime = maxBlockingTime;
    }

    /// <summary>BEST_EFFORT 既定値 (max_blocking_time = 0)。</summary>
    public static ReliabilityQos BestEffort { get; } = new(ReliabilityKind.BestEffort, Duration.Zero);

    /// <summary>RELIABLE 既定値 (max_blocking_time = 100ms)。</summary>
    public static ReliabilityQos Reliable { get; } = new(ReliabilityKind.Reliable, Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)));

    public bool Equals(ReliabilityQos other) => Kind == other.Kind && MaxBlockingTime.Equals(other.MaxBlockingTime);
    public override bool Equals(object? obj) => obj is ReliabilityQos r && Equals(r);
    public override int GetHashCode() => HashCode.Combine((int)Kind, MaxBlockingTime);
    public override string ToString() => $"Reliability({Kind}, blocking={MaxBlockingTime})";

    public static bool operator ==(ReliabilityQos left, ReliabilityQos right) => left.Equals(right);
    public static bool operator !=(ReliabilityQos left, ReliabilityQos right) => !left.Equals(right);
}
