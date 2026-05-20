using System.Net;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.Reader;
using Rclsharp.Transport;

namespace Rclsharp.Dds;

/// <summary>
/// <see cref="DomainParticipant"/> の構成オプション。
/// </summary>
public sealed class DomainParticipantOptions
{
    public int DomainId { get; init; }
    public int ParticipantId { get; init; } = 1;

    /// <summary>SPDP の送信間隔。既定 3 秒 (ROS 2 既定値)。</summary>
    public TimeSpan SpdpInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>SEDP (Publications/Subscriptions) の送信間隔。既定 3 秒。</summary>
    public TimeSpan SedpInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>SPDP の Lease Duration (この時間更新がなければ Lost と判定)。既定 20 秒。</summary>
    public Duration LeaseDuration { get; init; } = Duration.FromSeconds(20);

    /// <summary>マルチキャスト join に使うローカル NIC。null = ANY (全 NIC)。</summary>
    public IPAddress? MulticastInterface { get; init; }

    /// <summary>SPDP/Discovery 用マルチキャスト IPv4 アドレス。既定 239.255.0.1。</summary>
    public IPAddress MulticastGroup { get; init; } = RtpsConstants.DefaultMulticastAddress;

    /// <summary>
    /// MetatrafficUnicast Locator として広告するアドレス。null の場合は <see cref="IPAddress.Loopback"/> を使う。
    /// マルチホスト疎通を求める場合は実 NIC の IP を指定する。
    /// </summary>
    public IPAddress? LocalUnicastAddress { get; init; }

    /// <summary>Participant 名 (PID_ENTITY_NAME)。</summary>
    public string EntityName { get; init; } = "rclsharp_participant";

    public VendorId VendorId { get; init; } = VendorId.Rclsharp;
    public ProtocolVersion ProtocolVersion { get; init; } = ProtocolVersion.V2_4;

    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>DATA_FRAG 再構成バッファの制限値。</summary>
    public DataFragReassemblyOptions DataFragReassembly { get; init; } = DataFragReassemblyOptions.Default;

    /// <summary>
    /// テスト用の差し替えポイント。null なら <see cref="UdpTransport.CreateMulticast"/> で作る。
    /// </summary>
    public IRtpsTransport? CustomMulticastTransport { get; init; }

    /// <summary>
    /// テスト用の差し替えポイント。null なら <see cref="UdpTransport.CreateUnicast"/> で作る。
    /// </summary>
    public IRtpsTransport? CustomUnicastTransport { get; init; }

    /// <summary>
    /// ユーザートピック用 multicast transport の差し替えポイント (Phase 5)。null なら自動生成。
    /// </summary>
    public IRtpsTransport? CustomUserMulticastTransport { get; init; }

    /// <summary>
    /// ユーザートピック用 unicast transport の差し替えポイント。null なら自動生成。
    /// </summary>
    public IRtpsTransport? CustomUserUnicastTransport { get; init; }
}
