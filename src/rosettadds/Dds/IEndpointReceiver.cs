using ROSettaDDS.Common;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal interface IEndpointReceiver
{
    void RegisterWriter(EntityId writerEntityId, StatefulWriter writer);
    void UnregisterWriter(EntityId writerEntityId);
    void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler);
    void UnregisterReader(EntityId readerEntityId);
}
