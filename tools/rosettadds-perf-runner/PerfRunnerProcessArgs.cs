namespace ROSettaDDS.PerfRunner;

internal static class PerfRunnerProcessArgs
{
    internal const int HelperReadyTimeoutMs = 15000;

    internal static IReadOnlyList<string> Helper(
        PerfScenario scenario,
        string topic,
        string mode,
        bool measureStart)
    {
        var args = new List<string>
        {
            "--mode", mode,
            "--topic", topic,
            "--messages", scenario.Messages.ToString(),
            "--payload-bytes", scenario.PayloadBytes.ToString(),
            "--rate-hz", "0",
            "--qos", scenario.Qos,
            "--ready-timeout-ms", HelperReadyTimeoutMs.ToString(),
            "--idle-timeout-ms", "5000",
        };
        if (measureStart)
        {
            args.Add("--measure-start");
        }

        return args;
    }
}
