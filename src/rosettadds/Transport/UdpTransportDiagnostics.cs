namespace ROSettaDDS.Transport;

public readonly struct UdpTransportDiagnostics
{
    public UdpTransportDiagnostics(
        long datagramsReceived,
        long datagramsEnqueued,
        long datagramsDropped,
        long datagramsDispatched,
        int queueCount)
    {
        DatagramsReceived = datagramsReceived;
        DatagramsEnqueued = datagramsEnqueued;
        DatagramsDropped = datagramsDropped;
        DatagramsDispatched = datagramsDispatched;
        QueueCount = queueCount;
    }

    public long DatagramsReceived { get; }
    public long DatagramsEnqueued { get; }
    public long DatagramsDropped { get; }
    public long DatagramsDispatched { get; }
    public int QueueCount { get; }
}
