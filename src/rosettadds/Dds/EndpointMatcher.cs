using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds;

internal static class EndpointMatcher
{
    public static MatchDecision EvaluateLocalRemote(LocalWriter local, RemoteEndpoint remote)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remote.TypeName)
            || !QosCompatibility.IsCompatible(local.EndpointData, remote.Data))
        {
            return MatchDecision.NotCompatible;
        }
        return MatchDecision.Compatible(null, remote.Data.Reliability.Kind);
    }

    public static MatchDecision EvaluateLocalRemote(LocalReader local, RemoteEndpoint remote)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remote.TypeName)
            || !QosCompatibility.IsCompatible(remote.Data, local.EndpointData))
        {
            return MatchDecision.NotCompatible;
        }
        return MatchDecision.Compatible(null, null);
    }

    public static MatchDecision EvaluateLocalLocal(LocalReader reader, LocalWriter writer)
    {
        if (!TypeMatches(reader.EndpointData.TypeName, writer.EndpointData.TypeName)
            || !QosCompatibility.IsCompatible(writer.EndpointData, reader.EndpointData))
        {
            return MatchDecision.NotCompatible;
        }
        var writerLocator = FirstUdpLocator(writer.EndpointData.UnicastLocators);
        var readerLocator = FirstUdpLocator(reader.EndpointData.UnicastLocators);
        return MatchDecision.CompatibleLocalLocal(
            writerLocator,
            readerLocator,
            reader.EndpointData.Reliability.Kind);
    }

    public static Locator? ResolveRemoteUnicastLocator(
        RemoteEndpoint remote,
        IReadOnlyList<RemoteParticipant> participants)
    {
        var loc = FirstUdpLocator(remote.Data.UnicastLocators);
        if (loc is not null) return loc;
        foreach (var p in participants)
        {
            if (p.GuidPrefix.Equals(remote.Data.EndpointGuid.Prefix))
            {
                return FirstUdpLocator(p.Data.DefaultUnicastLocators);
            }
        }
        return null;
    }

    public static Locator? FirstUdpLocator(IEnumerable<Locator> locators)
    {
        foreach (var loc in locators)
        {
            if (loc.Kind is LocatorKind.UdpV4 or LocatorKind.UdpV6) return loc;
        }
        return null;
    }

    public static bool TypeMatches(string local, string remote)
        => !string.IsNullOrEmpty(local)
        && !string.IsNullOrEmpty(remote)
        && string.Equals(local, remote, StringComparison.Ordinal);
}
