using ROSettaDDS.Common;

namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// LatencyBudget QoS Policy。duration のみ (PL_CDR 8B Duration)。
/// </summary>
public readonly struct LatencyBudgetQos : IEquatable<LatencyBudgetQos>
{
    public Duration Duration { get; }

    public LatencyBudgetQos(Duration duration)
    {
        Duration = duration;
    }

    /// <summary>既定値 (duration = ZERO)。</summary>
    public static LatencyBudgetQos Default { get; } = new(Duration.Zero);

    public bool Equals(LatencyBudgetQos other) => Duration.Equals(other.Duration);
    public override bool Equals(object? obj) => obj is LatencyBudgetQos l && Equals(l);
    public override int GetHashCode() => Duration.GetHashCode();
    public override string ToString() => $"LatencyBudget({Duration})";

    public static bool operator ==(LatencyBudgetQos left, LatencyBudgetQos right) => left.Equals(right);
    public static bool operator !=(LatencyBudgetQos left, LatencyBudgetQos right) => !left.Equals(right);
}
