using ROSettaDDS.Common;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal sealed class ParticipantRtpsReceiverAdapter : IEndpointReceiver
{
    private readonly ParticipantRtpsReceiver _receiver;

    public ParticipantRtpsReceiverAdapter(ParticipantRtpsReceiver receiver)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
    }

    public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
        => _receiver.RegisterWriter(writerEntityId, writer);

    public void UnregisterWriter(EntityId writerEntityId)
        => _receiver.UnregisterWriter(writerEntityId);

    public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
        => _receiver.RegisterReader(readerEntityId, handler);

    public void UnregisterReader(EntityId readerEntityId)
        => _receiver.UnregisterReader(readerEntityId);
}
