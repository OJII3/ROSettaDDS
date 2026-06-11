using System.Net;

namespace ROSettaDDS.Transport;

/// <summary>
/// RTPS / DDS の標準定数。OMG DDS-RTPS 仕様 9.6.1.1 / 9.6.2.4。
/// </summary>
public static class RtpsConstants
{
    /// <summary>Port Base (PB)。SPDP 既定 7400。</summary>
    public const int PortBase = 7400;

    /// <summary>UDP port の最大値。</summary>
    public const int MaxUdpPort = ushort.MaxValue;

    /// <summary>Domain ID Gain (DG)。Domain あたり 250。</summary>
    public const int DomainGain = 250;

    /// <summary>Participant ID Gain (PG)。Participant あたり 2。</summary>
    public const int ParticipantGain = 2;

    /// <summary>Multicast metatraffic offset (d0)。SPDP マルチキャスト用。</summary>
    public const int OffsetMulticastMetatraffic = 0;

    /// <summary>Unicast metatraffic offset (d1)。SEDP ユニキャスト用。</summary>
    public const int OffsetUnicastMetatraffic = 10;

    /// <summary>Multicast user data offset (d2)。ユーザートピックのマルチキャスト用。</summary>
    public const int OffsetMulticastUserData = 1;

    /// <summary>Unicast user data offset (d3)。ユーザートピックのユニキャスト用。</summary>
    public const int OffsetUnicastUserData = 11;

    /// <summary>
    /// ROS 2 既定の Discovery マルチキャストアドレス (239.255.0.1)。
    /// rmw_fastrtps / rmw_cyclonedds 共通。
    /// </summary>
    public static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.0.1");

    /// <summary>有効な Domain ID 範囲 (0〜232)。<see cref="ushort.MaxValue"/> ポートに収まるよう制限。</summary>
    public const int MinDomainId = 0;
    public const int MaxDomainId = 232;

    /// <summary>有効な Participant ID 範囲 (0〜119)。最終的なポート範囲は domain との組み合わせで検証する。</summary>
    public const int MinParticipantId = 0;
    public const int MaxParticipantId = 119;
}
