using ROSettaDDS.Common;

namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Liveliness QoS Policy。kind + lease_duration (PL_CDR 12B)。
/// </summary>
public readonly struct LivelinessQos : IEquatable<LivelinessQos>
{
    public LivelinessKind Kind { get; }
    public Duration LeaseDuration { get; }

    public LivelinessQos(LivelinessKind kind, Duration leaseDuration)
    {
        Kind = kind;
        LeaseDuration = leaseDuration;
    }

    /// <summary>既定値 (AUTOMATIC, lease_duration = INFINITE)。</summary>
    public static LivelinessQos Default { get; } = new(LivelinessKind.Automatic, Duration.Infinite);

    public bool Equals(LivelinessQos other) => Kind == other.Kind && LeaseDuration.Equals(other.LeaseDuration);
    public override bool Equals(object? obj) => obj is LivelinessQos l && Equals(l);
    public override int GetHashCode() => HashCode.Combine((int)Kind, LeaseDuration);
    public override string ToString() => $"Liveliness({Kind}, lease={LeaseDuration})";

    public static bool operator ==(LivelinessQos left, LivelinessQos right) => left.Equals(right);
    public static bool operator !=(LivelinessQos left, LivelinessQos right) => !left.Equals(right);
}
