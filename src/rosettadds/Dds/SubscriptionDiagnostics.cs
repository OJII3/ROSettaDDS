using ROSettaDDS.Rtps.Reader;

namespace ROSettaDDS.Dds;

public readonly struct SubscriptionDiagnostics
{
    public SubscriptionDiagnostics(
        RtpsReaderDiagnostics rtpsReader,
        long payloadsReceivedFromReader,
        long messagesDeserialized,
        long deserializeFailures,
        long handlerInvocations)
    {
        RtpsReader = rtpsReader;
        PayloadsReceivedFromReader = payloadsReceivedFromReader;
        MessagesDeserialized = messagesDeserialized;
        DeserializeFailures = deserializeFailures;
        HandlerInvocations = handlerInvocations;
    }

    public RtpsReaderDiagnostics RtpsReader { get; }
    public long PayloadsReceivedFromReader { get; }
    public long MessagesDeserialized { get; }
    public long DeserializeFailures { get; }
    public long HandlerInvocations { get; }
}
