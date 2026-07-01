using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal sealed record LocalWriter(DiscoveredEndpointData EndpointData, StatefulWriter Writer);
