namespace ROSettaDDS.Dds.QoS;

/// <summary>
/// Partition QoS Policy。パーティション名の配列 (PL_CDR: sequence&lt;string&gt;)。
/// </summary>
public readonly struct PartitionQos : IEquatable<PartitionQos>
{
    private readonly string[]? _names;

    public IReadOnlyList<string> Names => _names ?? Array.Empty<string>();

    public PartitionQos(params string[] names)
    {
        _names = names.Length > 0 ? names : null;
    }

    /// <summary>既定値 (空パーティション)。</summary>
    public static PartitionQos Default { get; } = new();

    public bool Equals(PartitionQos other)
    {
        var a = _names ?? Array.Empty<string>();
        var b = other._names ?? Array.Empty<string>();
        return a.AsSpan().SequenceEqual(b);
    }

    public override bool Equals(object? obj) => obj is PartitionQos p && Equals(p);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var name in _names ?? Array.Empty<string>())
            hash.Add(name);
        return hash.ToHashCode();
    }

    public override string ToString() =>
        $"Partition([{string.Join(", ", _names ?? Array.Empty<string>())}])";

    public static bool operator ==(PartitionQos left, PartitionQos right) => left.Equals(right);
    public static bool operator !=(PartitionQos left, PartitionQos right) => !left.Equals(right);
}
