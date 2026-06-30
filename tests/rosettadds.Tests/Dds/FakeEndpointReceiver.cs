using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Dds;

internal sealed class FakeEndpointReceiver : IEndpointReceiver
{
    public List<(EntityId entityId, StatefulWriter writer)> RegisteredWriters { get; } = new();
    public List<EntityId> UnregisteredWriters { get; } = new();
    public List<(EntityId entityId, IRtpsSubmessageHandler handler)> RegisteredReaders { get; } = new();
    public List<EntityId> UnregisteredReaders { get; } = new();

    public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
    {
        RegisteredWriters.Add((writerEntityId, writer));
    }

    public void UnregisterWriter(EntityId writerEntityId)
    {
        UnregisteredWriters.Add(writerEntityId);
    }

    public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
    {
        RegisteredReaders.Add((readerEntityId, handler));
    }

    public void UnregisterReader(EntityId readerEntityId)
    {
        UnregisteredReaders.Add(readerEntityId);
    }
}
