namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Presentation QoS Policy。access_scope + coherent_access + ordered_access (PL_CDR 8B)。
/// </summary>
public readonly struct PresentationQos : IEquatable<PresentationQos>
{
    public PresentationAccessScope AccessScope { get; }
    public bool CoherentAccess { get; }
    public bool OrderedAccess { get; }

    public PresentationQos(PresentationAccessScope accessScope, bool coherentAccess, bool orderedAccess)
    {
        AccessScope = accessScope;
        CoherentAccess = coherentAccess;
        OrderedAccess = orderedAccess;
    }

    /// <summary>既定値 (INSTANCE, coherent=false, ordered=false)。</summary>
    public static PresentationQos Default { get; } = new(PresentationAccessScope.Instance, false, false);

    public bool Equals(PresentationQos other) =>
        AccessScope == other.AccessScope
        && CoherentAccess == other.CoherentAccess
        && OrderedAccess == other.OrderedAccess;

    public override bool Equals(object? obj) => obj is PresentationQos p && Equals(p);
    public override int GetHashCode() => HashCode.Combine((int)AccessScope, CoherentAccess, OrderedAccess);
    public override string ToString() => $"Presentation({AccessScope}, coherent={CoherentAccess}, ordered={OrderedAccess})";

    public static bool operator ==(PresentationQos left, PresentationQos right) => left.Equals(right);
    public static bool operator !=(PresentationQos left, PresentationQos right) => !left.Equals(right);
}
