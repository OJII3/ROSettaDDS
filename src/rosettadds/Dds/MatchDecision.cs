using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;

namespace ROSettaDDS.Dds;

internal readonly record struct MatchDecision(
    bool IsCompatible,
    Locator? UnicastLocator,
    Locator? SecondaryLocator,
    ReliabilityKind? ReliabilityKind)
{
    public static MatchDecision NotCompatible => new(false, null, null, null);
    public static MatchDecision Compatible(Locator? primaryLocator, ReliabilityKind? reliabilityKind)
        => new(true, primaryLocator, null, reliabilityKind);
    public static MatchDecision CompatibleLocalLocal(Locator? writerLocator, Locator? readerLocator, ReliabilityKind readerReliability)
        => new(true, writerLocator, readerLocator, readerReliability);
}
