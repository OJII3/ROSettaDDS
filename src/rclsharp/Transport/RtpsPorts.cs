namespace Rclsharp.Transport;

/// <summary>
/// Domain ID / Participant ID から RTPS の各種ポートを計算するヘルパ。
/// 計算式は OMG DDS-RTPS 仕様 9.6.1.1 / Table 9.8 に従う。
/// </summary>
public static class RtpsPorts
{
    /// <summary>SPDP マルチキャスト ポート: PB + DG*domainId + d0。</summary>
    public static int DiscoveryMulticast(int domainId)
    {
        ValidateDomain(domainId);
        return ValidatePort(
            RtpsConstants.PortBase
            + RtpsConstants.DomainGain * domainId
            + RtpsConstants.OffsetMulticastMetatraffic,
            domainId,
            participantId: null);
    }

    /// <summary>SEDP ユニキャスト ポート: PB + DG*domainId + d1 + PG*participantId。</summary>
    public static int DiscoveryUnicast(int domainId, int participantId)
    {
        ValidateDomain(domainId);
        ValidateParticipant(participantId);
        return ValidatePort(
            RtpsConstants.PortBase
            + RtpsConstants.DomainGain * domainId
            + RtpsConstants.OffsetUnicastMetatraffic
            + RtpsConstants.ParticipantGain * participantId,
            domainId,
            participantId);
    }

    /// <summary>ユーザートピック マルチキャスト ポート: PB + DG*domainId + d2。</summary>
    public static int UserMulticast(int domainId)
    {
        ValidateDomain(domainId);
        return ValidatePort(
            RtpsConstants.PortBase
            + RtpsConstants.DomainGain * domainId
            + RtpsConstants.OffsetMulticastUserData,
            domainId,
            participantId: null);
    }

    /// <summary>ユーザートピック ユニキャスト ポート: PB + DG*domainId + d3 + PG*participantId。</summary>
    public static int UserUnicast(int domainId, int participantId)
    {
        ValidateDomain(domainId);
        ValidateParticipant(participantId);
        return ValidatePort(
            RtpsConstants.PortBase
            + RtpsConstants.DomainGain * domainId
            + RtpsConstants.OffsetUnicastUserData
            + RtpsConstants.ParticipantGain * participantId,
            domainId,
            participantId);
    }

    private static void ValidateDomain(int domainId)
    {
        if (domainId < RtpsConstants.MinDomainId || domainId > RtpsConstants.MaxDomainId)
        {
            throw new ArgumentOutOfRangeException(nameof(domainId),
                $"Domain ID must be in [{RtpsConstants.MinDomainId}, {RtpsConstants.MaxDomainId}].");
        }
    }

    private static void ValidateParticipant(int participantId)
    {
        if (participantId < RtpsConstants.MinParticipantId || participantId > RtpsConstants.MaxParticipantId)
        {
            throw new ArgumentOutOfRangeException(nameof(participantId),
                $"Participant ID must be in [{RtpsConstants.MinParticipantId}, {RtpsConstants.MaxParticipantId}].");
        }
    }

    private static int ValidatePort(int port, int domainId, int? participantId)
    {
        if (port <= RtpsConstants.MaxUdpPort)
        {
            return port;
        }

        string scope = participantId.HasValue
            ? $"Domain ID {domainId} and Participant ID {participantId.Value}"
            : $"Domain ID {domainId}";
        throw new ArgumentOutOfRangeException(
            participantId.HasValue ? nameof(participantId) : nameof(domainId),
            $"The RTPS port for {scope} is {port}, which exceeds UDP port max {RtpsConstants.MaxUdpPort}.");
    }
}
