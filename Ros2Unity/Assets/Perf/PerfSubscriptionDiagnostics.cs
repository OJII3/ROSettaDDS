namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Subscription 診断値を perf 計測用に写像した値オブジェクト。
    /// </summary>
    internal readonly struct PerfSubscriptionDiagnostics
    {
        public long PayloadsReceivedFromReader { get; }
        public long MessagesDeserialized { get; }
        public long DeserializeFailures { get; }
        public long HandlerInvocations { get; }
        public long DataSubmessagesReceived { get; }
        public long DataFragSubmessagesReceived { get; }
        public long ReassembledPayloads { get; }
        public long PayloadsDelivered { get; }
        public long PayloadsBufferedPendingMatch { get; }
        public long PayloadsDropped { get; }

        public PerfSubscriptionDiagnostics(
            long payloadsReceivedFromReader,
            long messagesDeserialized,
            long deserializeFailures,
            long handlerInvocations,
            long dataSubmessagesReceived,
            long dataFragSubmessagesReceived,
            long reassembledPayloads,
            long payloadsDelivered,
            long payloadsBufferedPendingMatch,
            long payloadsDropped)
        {
            PayloadsReceivedFromReader = payloadsReceivedFromReader;
            MessagesDeserialized = messagesDeserialized;
            DeserializeFailures = deserializeFailures;
            HandlerInvocations = handlerInvocations;
            DataSubmessagesReceived = dataSubmessagesReceived;
            DataFragSubmessagesReceived = dataFragSubmessagesReceived;
            ReassembledPayloads = reassembledPayloads;
            PayloadsDelivered = payloadsDelivered;
            PayloadsBufferedPendingMatch = payloadsBufferedPendingMatch;
            PayloadsDropped = payloadsDropped;
        }
    }
}
