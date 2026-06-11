namespace ROSettaDDS.Common;

/// <summary>
/// RTPS Locator のトランスポート種別 (int32)。RTPS 仕様 9.3.2 / 9.6.1。
/// </summary>
public enum LocatorKind
{
    Invalid = -1,
    Reserved = 0,
    UdpV4 = 1,
    UdpV6 = 2,
    TcpV4 = 4,
    TcpV6 = 8,
    Shm = 0x01_00_00_00, // ベンダ拡張 (Fast-DDS Shared Memory)
}
