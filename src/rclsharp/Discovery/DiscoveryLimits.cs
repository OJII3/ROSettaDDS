using Rclsharp.Common;

namespace Rclsharp.Discovery;

/// <summary>
/// Discovery で受け入れる remote metadata と保持状態の上限。
/// </summary>
public sealed class DiscoveryLimits
{
    public static readonly DiscoveryLimits Default = new();

    public DiscoveryLimits(
        int maxRemoteParticipants = 128,
        int maxRemoteWriters = 1024,
        int maxRemoteReaders = 1024,
        int maxRemoteEndpointsPerParticipant = 256,
        int maxParticipantLocators = 32,
        int maxEndpointLocators = 16,
        int maxPartitionNames = 32,
        int maxEntityNameBytes = 256,
        int maxTopicNameBytes = 1024,
        int maxTypeNameBytes = 1024,
        int maxPartitionNameBytes = 256,
        double minRemoteParticipantLeaseSeconds = 1,
        double maxRemoteParticipantLeaseSeconds = 300)
    {
        ValidatePositive(maxRemoteParticipants, nameof(maxRemoteParticipants));
        ValidatePositive(maxRemoteWriters, nameof(maxRemoteWriters));
        ValidatePositive(maxRemoteReaders, nameof(maxRemoteReaders));
        ValidatePositive(maxRemoteEndpointsPerParticipant, nameof(maxRemoteEndpointsPerParticipant));
        ValidatePositive(maxParticipantLocators, nameof(maxParticipantLocators));
        ValidatePositive(maxEndpointLocators, nameof(maxEndpointLocators));
        ValidatePositive(maxPartitionNames, nameof(maxPartitionNames));
        ValidatePositive(maxEntityNameBytes, nameof(maxEntityNameBytes));
        ValidatePositive(maxTopicNameBytes, nameof(maxTopicNameBytes));
        ValidatePositive(maxTypeNameBytes, nameof(maxTypeNameBytes));
        ValidatePositive(maxPartitionNameBytes, nameof(maxPartitionNameBytes));

        if (minRemoteParticipantLeaseSeconds <= 0
            || double.IsNaN(minRemoteParticipantLeaseSeconds)
            || double.IsInfinity(minRemoteParticipantLeaseSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(minRemoteParticipantLeaseSeconds));
        }
        if (maxRemoteParticipantLeaseSeconds < minRemoteParticipantLeaseSeconds
            || double.IsNaN(maxRemoteParticipantLeaseSeconds)
            || double.IsInfinity(maxRemoteParticipantLeaseSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(maxRemoteParticipantLeaseSeconds));
        }

        MaxRemoteParticipants = maxRemoteParticipants;
        MaxRemoteWriters = maxRemoteWriters;
        MaxRemoteReaders = maxRemoteReaders;
        MaxRemoteEndpointsPerParticipant = maxRemoteEndpointsPerParticipant;
        MaxParticipantLocators = maxParticipantLocators;
        MaxEndpointLocators = maxEndpointLocators;
        MaxPartitionNames = maxPartitionNames;
        MaxEntityNameBytes = maxEntityNameBytes;
        MaxTopicNameBytes = maxTopicNameBytes;
        MaxTypeNameBytes = maxTypeNameBytes;
        MaxPartitionNameBytes = maxPartitionNameBytes;
        MinRemoteParticipantLeaseDuration = Duration.FromSeconds(minRemoteParticipantLeaseSeconds);
        MaxRemoteParticipantLeaseDuration = Duration.FromSeconds(maxRemoteParticipantLeaseSeconds);
    }

    public int MaxRemoteParticipants { get; }
    public int MaxRemoteWriters { get; }
    public int MaxRemoteReaders { get; }
    public int MaxRemoteEndpointsPerParticipant { get; }
    public int MaxParticipantLocators { get; }
    public int MaxEndpointLocators { get; }
    public int MaxPartitionNames { get; }
    public int MaxEntityNameBytes { get; }
    public int MaxTopicNameBytes { get; }
    public int MaxTypeNameBytes { get; }
    public int MaxPartitionNameBytes { get; }
    public Duration MinRemoteParticipantLeaseDuration { get; }
    public Duration MaxRemoteParticipantLeaseDuration { get; }

    public Duration ClampRemoteParticipantLeaseDuration(Duration value)
    {
        if (value.CompareTo(MinRemoteParticipantLeaseDuration) < 0)
        {
            return MinRemoteParticipantLeaseDuration;
        }
        if (value.CompareTo(MaxRemoteParticipantLeaseDuration) > 0)
        {
            return MaxRemoteParticipantLeaseDuration;
        }
        return value;
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
