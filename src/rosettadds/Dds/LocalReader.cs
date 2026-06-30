using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds;

internal sealed record LocalReader(DiscoveredEndpointData EndpointData, IUserReader Reader);
