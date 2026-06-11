using ROSettaDDS.Common;

namespace ROSettaDDS.Discovery;

/// <summary>
/// RTPS Built-in EntityId 定数。RTPS 仕様 9.3.1.5 / Table 9.2、および 8.5.4 (SPDP/SEDP)。
/// 値は 24bit key + 8bit kind の uint32 (BE 解釈)。
/// </summary>
public static class BuiltinEntityIds
{
    // -- Participant 自身 --
    /// <summary>ENTITYID_PARTICIPANT (0x000001c1)。</summary>
    public static readonly EntityId Participant = EntityId.Participant;

    // -- SPDP (Participant Discovery) --
    /// <summary>SPDP_BUILTIN_PARTICIPANT_WRITER (0x000100c2)。</summary>
    public static readonly EntityId SpdpBuiltinParticipantWriter = new(0x0001_00C2u);

    /// <summary>SPDP_BUILTIN_PARTICIPANT_READER (0x000100c7)。</summary>
    public static readonly EntityId SpdpBuiltinParticipantReader = new(0x0001_00C7u);

    // -- SEDP (Endpoint Discovery) --
    /// <summary>SEDP_BUILTIN_PUBLICATIONS_WRITER (0x000003c2)。</summary>
    public static readonly EntityId SedpBuiltinPublicationsWriter = new(0x0000_03C2u);

    /// <summary>SEDP_BUILTIN_PUBLICATIONS_READER (0x000003c7)。</summary>
    public static readonly EntityId SedpBuiltinPublicationsReader = new(0x0000_03C7u);

    /// <summary>SEDP_BUILTIN_SUBSCRIPTIONS_WRITER (0x000004c2)。</summary>
    public static readonly EntityId SedpBuiltinSubscriptionsWriter = new(0x0000_04C2u);

    /// <summary>SEDP_BUILTIN_SUBSCRIPTIONS_READER (0x000004c7)。</summary>
    public static readonly EntityId SedpBuiltinSubscriptionsReader = new(0x0000_04C7u);

    /// <summary>SEDP_BUILTIN_TOPIC_WRITER (0x000002c2)。ROS 2 では未使用。</summary>
    public static readonly EntityId SedpBuiltinTopicWriter = new(0x0000_02C2u);

    /// <summary>SEDP_BUILTIN_TOPIC_READER (0x000002c7)。ROS 2 では未使用。</summary>
    public static readonly EntityId SedpBuiltinTopicReader = new(0x0000_02C7u);

    // -- WLP (Writer Liveliness Protocol) --
    /// <summary>BUILTIN_PARTICIPANT_MESSAGE_WRITER (0x000200c2)。Liveliness 通知用。</summary>
    public static readonly EntityId BuiltinParticipantMessageWriter = new(0x0002_00C2u);

    /// <summary>BUILTIN_PARTICIPANT_MESSAGE_READER (0x000200c7)。</summary>
    public static readonly EntityId BuiltinParticipantMessageReader = new(0x0002_00C7u);
}

/// <summary>
/// BuiltinEndpointSet (PID_BUILTIN_ENDPOINT_SET) のビットフラグ。
/// RTPS 仕様 8.5.4.3 / Table 9.4。
/// 各ビットは "この Participant が対応する built-in endpoint" を示す。
/// </summary>
[Flags]
public enum BuiltinEndpointSet : uint
{
    None = 0u,

    DisableParticipantAnnouncement = 1u << 0, // unused

    ParticipantAnnouncer = 1u << 0,         // 0x00000001
    ParticipantDetector = 1u << 1,           // 0x00000002
    PublicationsAnnouncer = 1u << 2,         // 0x00000004
    PublicationsDetector = 1u << 3,          // 0x00000008
    SubscriptionsAnnouncer = 1u << 4,        // 0x00000010
    SubscriptionsDetector = 1u << 5,         // 0x00000020
    ParticipantProxyAnnouncer = 1u << 6,     // 0x00000040
    ParticipantProxyDetector = 1u << 7,      // 0x00000080
    ParticipantStateAnnouncer = 1u << 8,     // 0x00000100
    ParticipantStateDetector = 1u << 9,      // 0x00000200
    ParticipantMessageDataWriter = 1u << 10, // 0x00000400 (WLP 送信)
    ParticipantMessageDataReader = 1u << 11, // 0x00000800 (WLP 受信)

    /// <summary>rosettadds が現状サポートする最低限のセット (SPDP + SEDP Pub/Sub)。</summary>
    ROSettaDDSDefault =
        ParticipantAnnouncer
        | ParticipantDetector
        | PublicationsAnnouncer
        | PublicationsDetector
        | SubscriptionsAnnouncer
        | SubscriptionsDetector,
}
