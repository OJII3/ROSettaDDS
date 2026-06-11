namespace ROSettaDDS.Cdr.ParameterList;

/// <summary>
/// PL_CDR で使用される Parameter ID (PID, uint16)。
/// DDS-RTPS 仕様 9.6.2.2 / DDSI-RTPS 2.5 Annex C.5。
/// SPDP/SEDP/inline QoS で使う主要 PID を定義する。
/// </summary>
public static class ParameterId
{
    public const ushort Pad = 0x0000;
    public const ushort Sentinel = 0x0001;

    // ParticipantData / EndpointData 共通
    public const ushort UserData = 0x002C;
    public const ushort TopicName = 0x0005;
    public const ushort TypeName = 0x0007;
    public const ushort GroupData = 0x002D;
    public const ushort TopicData = 0x002E;

    // QoS
    public const ushort Durability = 0x001D;
    public const ushort DurabilityService = 0x001E;
    public const ushort Deadline = 0x0023;
    public const ushort LatencyBudget = 0x0027;
    public const ushort Liveliness = 0x001B;
    public const ushort Reliability = 0x001A;
    public const ushort Lifespan = 0x002B;
    public const ushort DestinationOrder = 0x0025;
    public const ushort History = 0x0040;
    public const ushort ResourceLimits = 0x0041;
    public const ushort Ownership = 0x001F;
    public const ushort OwnershipStrength = 0x0006;
    public const ushort Presentation = 0x0021;
    public const ushort Partition = 0x0029;
    public const ushort TimeBasedFilter = 0x0004;
    public const ushort TransportPriority = 0x0049;

    // RTPS / Discovery
    public const ushort ProtocolVersion = 0x0015;
    public const ushort VendorId = 0x0016;
    public const ushort UnicastLocator = 0x002F;
    public const ushort MulticastLocator = 0x0030;
    public const ushort DefaultUnicastLocator = 0x0031;
    public const ushort DefaultMulticastLocator = 0x0048;
    public const ushort MetatrafficUnicastLocator = 0x0032;
    public const ushort MetatrafficMulticastLocator = 0x0033;
    public const ushort ExpectsInlineQos = 0x0043;
    public const ushort ParticipantManualLivelinessCount = 0x0034;
    public const ushort ParticipantBuiltinEndpoints = 0x0044;
    public const ushort BuiltinEndpointSet = 0x0058;
    public const ushort ParticipantLeaseDuration = 0x0002;
    public const ushort ParticipantGuid = 0x0050;
    public const ushort ParticipantEntityId = 0x0051;
    public const ushort GroupGuid = 0x0052;
    public const ushort GroupEntityId = 0x0053;
    public const ushort BuiltinEndpointQos = 0x0077;
    public const ushort PropertyList = 0x0059;
    public const ushort EntityName = 0x0062;

    // Inline QoS / Endpoint
    public const ushort KeyHash = 0x0070;
    public const ushort StatusInfo = 0x0071;
    public const ushort EndpointGuid = 0x005A;

    // Domain
    public const ushort DomainId = 0x000F;
    public const ushort DomainTagBase = 0x0014;
    public const ushort DomainTag = MustUnderstandFlag | DomainTagBase;

    // Type information (XTYPES)
    public const ushort TypeMaxSizeSerialized = 0x0060;

    /// <summary>
    /// Vendor-specific PID 領域の開始 (0x8000)。
    /// 0x8000 以上は実装ベンダ固有として扱う。受信側は不明な PID をスキップしてよい。
    /// </summary>
    public const ushort VendorSpecificStart = 0x8000;
    public const ushort VendorSpecificFlag = 0x8000;

    /// <summary>
    /// 必須フラグ (0x4000)。set されている場合は受信側が解釈できないと拒否すべき。
    /// PID 値の bit 14。
    /// </summary>
    public const ushort MustUnderstandFlag = 0x4000;

    public static bool IsVendorSpecific(ushort pid) => (pid & VendorSpecificFlag) != 0;
    public static bool IsMustUnderstand(ushort pid) => (pid & MustUnderstandFlag) != 0;

    /// <summary>
    /// MustUnderstand ビットだけを除いた PID を返す。
    /// Vendor-specific ビットは PID の同一性なので保持し、標準 PID と衝突させない。
    /// </summary>
    public static ushort StripFlags(ushort pid) => (ushort)(pid & ~MustUnderstandFlag);

    public static void ThrowIfUnknownMustUnderstand(ushort pid)
    {
        if (IsMustUnderstand(pid))
        {
            throw new InvalidDataException($"Unknown must-understand PID 0x{pid:X4}.");
        }
    }
}
