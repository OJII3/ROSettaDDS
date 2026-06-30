using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal readonly record struct EndpointSnapshot(StatefulWriter[] Writers, IUserReader[] Readers);
