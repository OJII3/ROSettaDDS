using ROSettaDDS.Common;

namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Deadline QoS Policy。period のみ (PL_CDR 8B Duration)。
/// </summary>
public readonly struct DeadlineQos : IEquatable<DeadlineQos>
{
    public Duration Period { get; }

    public DeadlineQos(Duration period)
    {
        Period = period;
    }

    /// <summary>既定値 (period = INFINITE)。</summary>
    public static DeadlineQos Default { get; } = new(Duration.Infinite);

    public bool Equals(DeadlineQos other) => Period.Equals(other.Period);
    public override bool Equals(object? obj) => obj is DeadlineQos d && Equals(d);
    public override int GetHashCode() => Period.GetHashCode();
    public override string ToString() => $"Deadline({Period})";

    public static bool operator ==(DeadlineQos left, DeadlineQos right) => left.Equals(right);
    public static bool operator !=(DeadlineQos left, DeadlineQos right) => !left.Equals(right);
}
