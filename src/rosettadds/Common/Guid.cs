namespace ROSettaDDS.Common;

/// <summary>
/// RTPS GUID (16 バイト = GuidPrefix 12 + EntityId 4)。RTPS 仕様 8.2.4.1。
/// ※ <see cref="System.Guid"/> とは別物。名前空間 ROSettaDDS.Common 内で使う。
/// </summary>
public readonly struct Guid : IEquatable<Guid>
{
    public const int Size = GuidPrefix.Size + EntityId.Size;

    public GuidPrefix Prefix { get; }
    public EntityId EntityId { get; }

    public Guid(GuidPrefix prefix, EntityId entityId)
    {
        Prefix = prefix;
        EntityId = entityId;
    }

    /// <summary>GUID_UNKNOWN (全 0)。</summary>
    public static Guid Unknown => default;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"Destination requires at least {Size} bytes.", nameof(destination));
        }
        Prefix.CopyTo(destination[..GuidPrefix.Size]);
        EntityId.WriteTo(destination[GuidPrefix.Size..]);
    }

    public static Guid Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"Source requires at least {Size} bytes.", nameof(source));
        }
        var prefix = new GuidPrefix(source[..GuidPrefix.Size]);
        var entityId = EntityId.Read(source[GuidPrefix.Size..]);
        return new Guid(prefix, entityId);
    }

    public bool Equals(Guid other) => Prefix.Equals(other.Prefix) && EntityId.Equals(other.EntityId);
    public override bool Equals(object? obj) => obj is Guid g && Equals(g);
    public override int GetHashCode() => HashCode.Combine(Prefix, EntityId);
    public override string ToString() => $"{Prefix}.{EntityId.Value:X8}";

    public static bool operator ==(Guid left, Guid right) => left.Equals(right);
    public static bool operator !=(Guid left, Guid right) => !left.Equals(right);
}
