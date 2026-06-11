namespace ROSettaDDS.Common;

/// <summary>
/// RTPS プロトコルバージョン (DDSI-RTPS 2.x)。
/// メジャー/マイナー各 1 バイト。
/// </summary>
public readonly struct ProtocolVersion : IEquatable<ProtocolVersion>
{
    public byte Major { get; }
    public byte Minor { get; }

    public ProtocolVersion(byte major, byte minor)
    {
        Major = major;
        Minor = minor;
    }

    /// <summary>RTPS 2.1 (DDS 1.2 と互換)</summary>
    public static readonly ProtocolVersion V2_1 = new(2, 1);

    /// <summary>RTPS 2.2</summary>
    public static readonly ProtocolVersion V2_2 = new(2, 2);

    /// <summary>RTPS 2.3</summary>
    public static readonly ProtocolVersion V2_3 = new(2, 3);

    /// <summary>RTPS 2.4 (ROS 2 既定)</summary>
    public static readonly ProtocolVersion V2_4 = new(2, 4);

    /// <summary>rosettadds 既定バージョン (= V2_4)</summary>
    public static readonly ProtocolVersion Current = V2_4;

    public bool Equals(ProtocolVersion other) => Major == other.Major && Minor == other.Minor;
    public override bool Equals(object? obj) => obj is ProtocolVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor);
    public override string ToString() => $"{Major}.{Minor}";

    public static bool operator ==(ProtocolVersion left, ProtocolVersion right) => left.Equals(right);
    public static bool operator !=(ProtocolVersion left, ProtocolVersion right) => !left.Equals(right);
}
