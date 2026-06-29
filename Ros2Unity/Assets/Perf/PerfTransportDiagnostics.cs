namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// UdpTransport の diagnostics 値を perf 計測用に transport 抽象へ写像した値オブジェクト。
    /// `Available = false` のときは該当 transport が UdpTransport ではない (loopback 等)。
    /// </summary>
    internal readonly struct PerfTransportDiagnostics
    {
        public bool Available { get; }
        public long DatagramsReceived { get; }
        public long DatagramsEnqueued { get; }
        public long DatagramsDropped { get; }
        public long DatagramsDispatched { get; }
        public long QueueCount { get; }

        public PerfTransportDiagnostics(
            bool available,
            long datagramsReceived,
            long datagramsEnqueued,
            long datagramsDropped,
            long datagramsDispatched,
            long queueCount)
        {
            Available = available;
            DatagramsReceived = datagramsReceived;
            DatagramsEnqueued = datagramsEnqueued;
            DatagramsDropped = datagramsDropped;
            DatagramsDispatched = datagramsDispatched;
            QueueCount = queueCount;
        }

        public static PerfTransportDiagnostics Unavailable() => new PerfTransportDiagnostics(false, 0L, 0L, 0L, 0L, 0L);
    }
}
