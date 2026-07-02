using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Context"/> の構成オプション。
/// <see cref="ROSettaDDS.Dds.DomainParticipantOptions"/> の DDS 資源に関するプロパティを
/// そのまま受け継ぐ。public な正本はこちら。
/// </summary>
public sealed class ContextOptions
{
    public int DomainId { get; init; }
    public int ParticipantId { get; init; } = 0;
    public bool AutoProbeParticipantId { get; init; } = true;
    public TimeSpan SpdpInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan SedpInterval { get; init; } = TimeSpan.FromSeconds(3);
    public Duration LeaseDuration { get; init; } = Duration.FromSeconds(20);
    public TimeSpan UserWriterHeartbeatPeriod { get; init; } = TimeSpan.FromSeconds(1);
    public int UserWriterHistoryDepth { get; init; } = 1000;
    public IPAddress? MulticastInterface { get; init; }
    public IPAddress MulticastGroup { get; init; } = RtpsConstants.DefaultMulticastAddress;
    public IPAddress? LocalUnicastAddress { get; init; }
    public bool LocalhostOnly { get; init; }
    public string EntityName { get; init; } = "rosettadds_context";
    public VendorId VendorId { get; init; } = VendorId.ROSettaDDS;
    public ProtocolVersion ProtocolVersion { get; init; } = ProtocolVersion.V2_4;
    public ILogger Logger { get; init; } = NullLogger.Instance;
    public DataFragReassemblyOptions DataFragReassembly { get; init; } = DataFragReassemblyOptions.Default;
    public CdrReadLimits CdrReadLimits { get; init; } = CdrReadLimits.Default;
    public DiscoveryLimits DiscoveryLimits { get; init; } = DiscoveryLimits.Default;
    public IRtpsTransport? CustomMulticastTransport { get; init; }
    public IRtpsTransport? CustomUnicastTransport { get; init; }
    public IRtpsTransport? CustomUserMulticastTransport { get; init; }
    public IRtpsTransport? CustomUserUnicastTransport { get; init; }
}
