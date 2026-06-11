using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Discovery;

/// <summary>
/// SPDP (Simple Participant Discovery Protocol) で交換される ParticipantData。
/// RTPS 仕様 8.5.4.2 / 9.6.2.2.2 / DDS-XTYPES Annex B。
/// PL_CDR でシリアライズされ、SPDPdiscoveredParticipantData トピックの DATA submessage に乗る。
/// </summary>
public sealed class ParticipantData
{
    /// <summary>RTPS Protocol Version (PID_PROTOCOL_VERSION)。必須。</summary>
    public ProtocolVersion ProtocolVersion { get; set; } = ProtocolVersion.V2_4;

    /// <summary>Vendor ID (PID_VENDORID)。必須。</summary>
    public VendorId VendorId { get; set; } = VendorId.ROSettaDDS;

    /// <summary>Participant の Guid (PID_PARTICIPANT_GUID)。必須。</summary>
    public Guid Guid { get; set; }

    /// <summary>サポートする built-in endpoint の bitmask (PID_BUILTIN_ENDPOINT_SET)。必須。</summary>
    public BuiltinEndpointSet BuiltinEndpoints { get; set; } = BuiltinEndpointSet.ROSettaDDSDefault;

    /// <summary>Lease 期限 (PID_PARTICIPANT_LEASE_DURATION)。既定 20 秒。</summary>
    public Duration LeaseDuration { get; set; } = Duration.FromSeconds(20);

    /// <summary>SEDP/Discovery 用 unicast Locator (PID_METATRAFFIC_UNICAST_LOCATOR)。複数可。</summary>
    public List<Locator> MetatrafficUnicastLocators { get; } = new();

    /// <summary>SPDP/Discovery 用 multicast Locator (PID_METATRAFFIC_MULTICAST_LOCATOR)。複数可。</summary>
    public List<Locator> MetatrafficMulticastLocators { get; } = new();

    /// <summary>ユーザーデータ用 unicast Locator (PID_DEFAULT_UNICAST_LOCATOR)。複数可。</summary>
    public List<Locator> DefaultUnicastLocators { get; } = new();

    /// <summary>ユーザーデータ用 multicast Locator (PID_DEFAULT_MULTICAST_LOCATOR)。複数可。</summary>
    public List<Locator> DefaultMulticastLocators { get; } = new();

    /// <summary>Inline QoS を期待する (PID_EXPECTS_INLINE_QOS)。既定 false。</summary>
    public bool ExpectsInlineQos { get; set; }

    /// <summary>Participant の名前 (PID_ENTITY_NAME)。任意。</summary>
    public string? EntityName { get; set; }
}
