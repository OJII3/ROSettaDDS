namespace ROSettaDDS.Rtps.Reader;

public readonly struct RtpsReaderDiagnostics
{
    public RtpsReaderDiagnostics(
        long dataSubmessagesReceived,
        long dataFragSubmessagesReceived,
        long reassembledPayloads,
        long payloadsDelivered,
        long payloadsBufferedPendingMatch,
        long payloadsDropped)
    {
        DataSubmessagesReceived = dataSubmessagesReceived;
        DataFragSubmessagesReceived = dataFragSubmessagesReceived;
        ReassembledPayloads = reassembledPayloads;
        PayloadsDelivered = payloadsDelivered;
        PayloadsBufferedPendingMatch = payloadsBufferedPendingMatch;
        PayloadsDropped = payloadsDropped;
    }

    public long DataSubmessagesReceived { get; }
    public long DataFragSubmessagesReceived { get; }
    public long ReassembledPayloads { get; }
    public long PayloadsDelivered { get; }
    public long PayloadsBufferedPendingMatch { get; }
    public long PayloadsDropped { get; }
}
