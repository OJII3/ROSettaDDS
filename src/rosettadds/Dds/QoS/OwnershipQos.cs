namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Ownership QoS Policy。kind のみ (PL_CDR 4B)。
/// </summary>
public readonly struct OwnershipQos : IEquatable<OwnershipQos>
{
    public OwnershipKind Kind { get; }

    public OwnershipQos(OwnershipKind kind)
    {
        Kind = kind;
    }

    /// <summary>既定値 (SHARED)。</summary>
    public static OwnershipQos Default { get; } = new(OwnershipKind.Shared);

    public bool Equals(OwnershipQos other) => Kind == other.Kind;
    public override bool Equals(object? obj) => obj is OwnershipQos o && Equals(o);
    public override int GetHashCode() => (int)Kind;
    public override string ToString() => $"Ownership({Kind})";

    public static bool operator ==(OwnershipQos left, OwnershipQos right) => left.Equals(right);
    public static bool operator !=(OwnershipQos left, OwnershipQos right) => !left.Equals(right);
}
