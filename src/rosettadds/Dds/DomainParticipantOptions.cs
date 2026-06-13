using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Dds;

/// <summary>
/// <see cref="DomainParticipant"/> の構成オプション。
/// </summary>
public sealed class DomainParticipantOptions
{
    public int DomainId { get; init; }
    public int ParticipantId { get; init; } = 0;

    /// <summary>
    /// true の場合、指定した <see cref="ParticipantId"/> のユニキャストポートが使用中なら
    /// 空きが見つかるまで ID をインクリメントして再試行する。既定 true。
    /// </summary>
    public bool AutoProbeParticipantId { get; init; } = true;

    /// <summary>SPDP の送信間隔。既定 3 秒 (ROS 2 既定値)。</summary>
    public TimeSpan SpdpInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>SEDP (Publications/Subscriptions) の送信間隔。既定 3 秒。</summary>
    public TimeSpan SedpInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>SPDP の Lease Duration (この時間更新がなければ Lost と判定)。既定 20 秒。</summary>
    public Duration LeaseDuration { get; init; } = Duration.FromSeconds(20);

    /// <summary>ユーザートピック Publisher の HEARTBEAT 送信間隔。既定 1 秒。</summary>
    public TimeSpan UserWriterHeartbeatPeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// ユーザートピック Publisher の WriterHistoryCache が保持する最大 sample 数
    /// (KeepLast depth 相当)。既定 1000。
    /// </summary>
    public int UserWriterHistoryDepth { get; init; } = 1000;

    /// <summary>マルチキャスト join に使うローカル NIC。null = ANY (全 NIC)。</summary>
    public IPAddress? MulticastInterface { get; init; }

    /// <summary>SPDP/Discovery 用マルチキャスト IPv4 アドレス。既定 239.255.0.1。</summary>
    public IPAddress MulticastGroup { get; init; } = RtpsConstants.DefaultMulticastAddress;

    /// <summary>
    /// Unicast Locator として広告するアドレス。
    /// <para>
    /// null (既定) の場合は、Fast DDS / Cyclone DDS の rmw 層と同様に
    /// 稼働中の全 NIC の IPv4 アドレス (loopback を含む) を自動列挙して広告し、
    /// unicast ソケットは <see cref="IPAddress.Any"/> にバインドして全 NIC で受信する。
    /// </para>
    /// <para>
    /// 特定 NIC のみを広告したい場合はその IP を明示指定する。
    /// loopback に限定したい場合は <see cref="LocalhostOnly"/> を使う。
    /// </para>
    /// </summary>
    public IPAddress? LocalUnicastAddress { get; init; }

    /// <summary>
    /// ROS_LOCALHOST_ONLY 相当。true の場合、unicast/multicast を loopback に限定する
    /// (<see cref="LocalUnicastAddress"/> より優先)。既定 false。
    /// </summary>
    public bool LocalhostOnly { get; init; }

    /// <summary>Participant 名 (PID_ENTITY_NAME)。</summary>
    public string EntityName { get; init; } = "rosettadds_participant";

    public VendorId VendorId { get; init; } = VendorId.ROSettaDDS;
    public ProtocolVersion ProtocolVersion { get; init; } = ProtocolVersion.V2_4;

    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>DATA_FRAG 再構成バッファの制限値。</summary>
    public DataFragReassemblyOptions DataFragReassembly { get; init; } = DataFragReassemblyOptions.Default;

    /// <summary>user data payload を CDR デシリアライズするときの読み取り上限。</summary>
    public CdrReadLimits CdrReadLimits { get; init; } = CdrReadLimits.Default;

    /// <summary>remote discovery metadata と保持状態の制限値。</summary>
    public DiscoveryLimits DiscoveryLimits { get; init; } = DiscoveryLimits.Default;

    /// <summary>
    /// テスト用の差し替えポイント。null なら <see cref="UdpTransport.CreateMulticast"/> で作る。
    /// </summary>
    public IRtpsTransport? CustomMulticastTransport { get; init; }

    /// <summary>
    /// テスト用の差し替えポイント。null なら <see cref="UdpTransport.CreateUnicast"/> で作る。
    /// </summary>
    public IRtpsTransport? CustomUnicastTransport { get; init; }

    /// <summary>
    /// ユーザートピック用 multicast transport の差し替えポイント。null なら自動生成。
    /// </summary>
    public IRtpsTransport? CustomUserMulticastTransport { get; init; }

    /// <summary>
    /// ユーザートピック用 unicast transport の差し替えポイント。null なら自動生成。
    /// </summary>
    public IRtpsTransport? CustomUserUnicastTransport { get; init; }
}
