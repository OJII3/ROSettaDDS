namespace ROSettaDDS.Common;

/// <summary>
/// RTPS Vendor ID (実装ベンダ識別子)。2 バイト固定。
/// OMG が登録管理。https://www.dds-foundation.org/dds-vendors/
/// </summary>
public readonly struct VendorId : IEquatable<VendorId>
{
    public byte V0 { get; }
    public byte V1 { get; }

    public VendorId(byte v0, byte v1)
    {
        V0 = v0;
        V1 = v1;
    }

    /// <summary>未指定</summary>
    public static readonly VendorId Unknown = new(0x00, 0x00);

    /// <summary>RTI Connext DDS</summary>
    public static readonly VendorId RtiConnext = new(0x01, 0x01);

    /// <summary>OCI OpenDDS</summary>
    public static readonly VendorId OciOpenDds = new(0x01, 0x03);

    /// <summary>eProsima Fast DDS (rmw_fastrtps_cpp デフォルト)</summary>
    public static readonly VendorId EProsimaFastDds = new(0x01, 0x0F);

    /// <summary>Eclipse Cyclone DDS (rmw_cyclonedds_cpp)</summary>
    public static readonly VendorId EclipseCycloneDds = new(0x01, 0x10);

    /// <summary>
    /// rosettadds の既定 Vendor ID。
    /// 当面は eProsima Fast-DDS の値を借用 (ROS 2 ツール群と互換性確認のため)。
    /// 動作検証完了後に独自 ID へ切替予定。
    /// </summary>
    public static readonly VendorId ROSettaDDS = EProsimaFastDds;

    public bool Equals(VendorId other) => V0 == other.V0 && V1 == other.V1;
    public override bool Equals(object? obj) => obj is VendorId v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(V0, V1);
    public override string ToString() => $"0x{V0:X2}{V1:X2}";

    public static bool operator ==(VendorId left, VendorId right) => left.Equals(right);
    public static bool operator !=(VendorId left, VendorId right) => !left.Equals(right);
}
