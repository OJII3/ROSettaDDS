using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

public readonly record struct PublicationMatchedStatus(
    int TotalCount = 0,
    int TotalCountChange = 0,
    int CurrentCount = 0,
    int CurrentCountChange = 0,
    Guid? LastSubscriptionHandle = null)
{
    public override string ToString() =>
        $"PublicationMatched(current={CurrentCount}, currentChange={CurrentCountChange}, " +
        $"total={TotalCount}, totalChange={TotalCountChange}, " +
        $"lastHandle={LastSubscriptionHandle})";
}

public readonly record struct SubscriptionMatchedStatus(
    int TotalCount = 0,
    int TotalCountChange = 0,
    int CurrentCount = 0,
    int CurrentCountChange = 0,
    Guid? LastPublicationHandle = null)
{
    public override string ToString() =>
        $"SubscriptionMatched(current={CurrentCount}, currentChange={CurrentCountChange}, " +
        $"total={TotalCount}, totalChange={TotalCountChange}, " +
        $"lastHandle={LastPublicationHandle})";
}
