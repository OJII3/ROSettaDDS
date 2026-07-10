using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal readonly record struct EndpointSnapshot(StatefulWriter[] Writers, IUserReader[] Readers);

internal readonly record struct EndpointDiscoverySnapshot(
    DiscoveredEndpointData[] Writers,
    DiscoveredEndpointData[] Readers);
