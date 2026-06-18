using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// Publication マッチ状態 (Fast DDS PublicationMatchedStatus 互換)。
/// <see cref="Publisher{T}.PublicationMatchedStatus"/> から取得する。
/// <para>
/// <c>TotalCountChange</c> と <c>CurrentCountChange</c> は「最後にこの構造体を
/// read したときからの差分」を表し、read 時に 0 にリセットされる
/// (Fast DDS <c>get_publication_matched_status</c> 互換)。
/// </para>
/// </summary>
public readonly struct PublicationMatchedStatus : IEquatable<PublicationMatchedStatus>
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }

    /// <summary>最後にマッチした remote reader の GUID (ある場合)。</summary>
    public Guid? LastSubscriptionHandle { get; init; }

    public bool Equals(PublicationMatchedStatus other) =>
        TotalCount == other.TotalCount &&
        TotalCountChange == other.TotalCountChange &&
        CurrentCount == other.CurrentCount &&
        CurrentCountChange == other.CurrentCountChange &&
        Nullable.Equals(LastSubscriptionHandle, other.LastSubscriptionHandle);

    public override bool Equals(object? obj) => obj is PublicationMatchedStatus s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(TotalCount, TotalCountChange, CurrentCount, CurrentCountChange, LastSubscriptionHandle);
    public override string ToString() =>
        $"PublicationMatched(current={CurrentCount}, currentChange={CurrentCountChange}, " +
        $"total={TotalCount}, totalChange={TotalCountChange}, " +
        $"lastHandle={LastSubscriptionHandle})";

    public static bool operator ==(PublicationMatchedStatus left, PublicationMatchedStatus right) => left.Equals(right);
    public static bool operator !=(PublicationMatchedStatus left, PublicationMatchedStatus right) => !left.Equals(right);
}

/// <summary>
/// Subscription マッチ状態 (Fast DDS SubscriptionMatchedStatus 互換)。
/// <see cref="Subscription{T}.SubscriptionMatchedStatus"/> から取得する。
/// <para>
/// <c>TotalCountChange</c> と <c>CurrentCountChange</c> は「最後にこの構造体を
/// read したときからの差分」を表し、read 時に 0 にリセットされる
/// (Fast DDS <c>get_subscription_matched_status</c> 互換)。
/// </para>
/// </summary>
public readonly struct SubscriptionMatchedStatus : IEquatable<SubscriptionMatchedStatus>
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }

    /// <summary>最後にマッチした remote writer の GUID (ある場合)。</summary>
    public Guid? LastPublicationHandle { get; init; }

    public bool Equals(SubscriptionMatchedStatus other) =>
        TotalCount == other.TotalCount &&
        TotalCountChange == other.TotalCountChange &&
        CurrentCount == other.CurrentCount &&
        CurrentCountChange == other.CurrentCountChange &&
        Nullable.Equals(LastPublicationHandle, other.LastPublicationHandle);

    public override bool Equals(object? obj) => obj is SubscriptionMatchedStatus s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(TotalCount, TotalCountChange, CurrentCount, CurrentCountChange, LastPublicationHandle);
    public override string ToString() =>
        $"SubscriptionMatched(current={CurrentCount}, currentChange={CurrentCountChange}, " +
        $"total={TotalCount}, totalChange={TotalCountChange}, " +
        $"lastHandle={LastPublicationHandle})";

    public static bool operator ==(SubscriptionMatchedStatus left, SubscriptionMatchedStatus right) => left.Equals(right);
    public static bool operator !=(SubscriptionMatchedStatus left, SubscriptionMatchedStatus right) => !left.Equals(right);
}
