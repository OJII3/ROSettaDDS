namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Durability QoS Policy。kind のみ (PL_CDR 4B)。
/// </summary>
public readonly struct DurabilityQos : IEquatable<DurabilityQos>
{
    public DurabilityKind Kind { get; }

    public DurabilityQos(DurabilityKind kind)
    {
        Kind = kind;
    }

    public static DurabilityQos Volatile { get; } = new(DurabilityKind.Volatile);
    public static DurabilityQos TransientLocal { get; } = new(DurabilityKind.TransientLocal);

    public bool Equals(DurabilityQos other) => Kind == other.Kind;
    public override bool Equals(object? obj) => obj is DurabilityQos d && Equals(d);
    public override int GetHashCode() => (int)Kind;
    public override string ToString() => $"Durability({Kind})";

    public static bool operator ==(DurabilityQos left, DurabilityQos right) => left.Equals(right);
    public static bool operator !=(DurabilityQos left, DurabilityQos right) => !left.Equals(right);
}
