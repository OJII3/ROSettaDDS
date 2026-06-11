namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// DestinationOrder QoS Policy。kind のみ (PL_CDR 4B)。
/// </summary>
public readonly struct DestinationOrderQos : IEquatable<DestinationOrderQos>
{
    public DestinationOrderKind Kind { get; }

    public DestinationOrderQos(DestinationOrderKind kind)
    {
        Kind = kind;
    }

    /// <summary>既定値 (BY_RECEPTION_TIMESTAMP)。</summary>
    public static DestinationOrderQos Default { get; } = new(DestinationOrderKind.ByReceptionTimestamp);

    public bool Equals(DestinationOrderQos other) => Kind == other.Kind;
    public override bool Equals(object? obj) => obj is DestinationOrderQos d && Equals(d);
    public override int GetHashCode() => (int)Kind;
    public override string ToString() => $"DestinationOrder({Kind})";

    public static bool operator ==(DestinationOrderQos left, DestinationOrderQos right) => left.Equals(right);
    public static bool operator !=(DestinationOrderQos left, DestinationOrderQos right) => !left.Equals(right);
}
