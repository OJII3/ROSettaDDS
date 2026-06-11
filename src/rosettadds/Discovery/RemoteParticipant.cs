using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Discovery;

/// <summary>
/// 検出した remote Participant の状態。
/// SPDP で受信した <see cref="ParticipantData"/> と最終受信時刻を保持。
/// Lease 期限切れ判定や locator 解決に使う。
/// </summary>
public sealed class RemoteParticipant
{
    public ParticipantData Data { get; private set; }
    public DateTime FirstSeenUtc { get; }
    public DateTime LastSeenUtc { get; private set; }

    public Guid Guid => Data.Guid;
    public GuidPrefix GuidPrefix => Data.Guid.Prefix;

    public RemoteParticipant(ParticipantData data, DateTime nowUtc)
    {
        Data = data;
        FirstSeenUtc = nowUtc;
        LastSeenUtc = nowUtc;
    }

    /// <summary>新しい SPDP データを受信したときに状態を更新する。</summary>
    public void Update(ParticipantData data, DateTime nowUtc)
    {
        Data = data;
        LastSeenUtc = nowUtc;
    }

    /// <summary>Lease 期限切れか。</summary>
    public bool IsExpired(DateTime nowUtc)
    {
        return nowUtc - LastSeenUtc > Data.LeaseDuration.ToTimeSpan();
    }

    public override string ToString()
        => $"RemoteParticipant({Guid}, name={Data.EntityName ?? "<null>"}, lastSeen={LastSeenUtc:O})";
}
