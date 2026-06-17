using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Common;

/// <summary>
/// Fast DDS RPC の SampleIdentity (writer GUID + sequence number)。wire 上 24 バイト。
/// サービスの request/reply 相関 (related_sample_identity) に使う。
/// </summary>
public readonly struct SampleIdentity : IEquatable<SampleIdentity>
{
    public const int Size = 24; // Guid 16 + SequenceNumber 8

    public Guid Writer { get; }
    public SequenceNumber SequenceNumber { get; }

    public SampleIdentity(Guid writer, SequenceNumber sequenceNumber)
    {
        Writer = writer;
        SequenceNumber = sequenceNumber;
    }

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination requires at least {Size} bytes.", nameof(destination));
        Writer.WriteTo(destination[..16]);
        SequenceNumber.WriteTo(destination.Slice(16, 8), littleEndian);
    }

    public static SampleIdentity Read(ReadOnlySpan<byte> source, bool littleEndian)
    {
        if (source.Length < Size)
            throw new ArgumentException($"Source requires at least {Size} bytes.", nameof(source));
        var writer = Guid.Read(source[..16]);
        var sn = SequenceNumber.Read(source.Slice(16, 8), littleEndian);
        return new SampleIdentity(writer, sn);
    }

    public bool Equals(SampleIdentity other) => Writer.Equals(other.Writer) && SequenceNumber.Equals(other.SequenceNumber);
    public override bool Equals(object? obj) => obj is SampleIdentity s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Writer, SequenceNumber);
    public override string ToString() => $"SampleIdentity({Writer}, {SequenceNumber})";

    public static bool operator ==(SampleIdentity left, SampleIdentity right) => left.Equals(right);
    public static bool operator !=(SampleIdentity left, SampleIdentity right) => !left.Equals(right);
}
