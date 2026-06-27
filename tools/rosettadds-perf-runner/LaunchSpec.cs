namespace ROSettaDDS.PerfRunner;

internal enum LogKind
{
    Stdout,
    Stderr,
}

internal sealed record LaunchSpec(
    string Kind,
    string ScenarioName,
    string Direction,
    int DomainId,
    string Topic,
    string Qos,
    int PayloadBytes,
    int Messages,
    bool LocalhostOnly,
    string ReadyFile,
    string DoneFile,
    string? ReleaseFile,
    string MetricsFile,
    string PlayerExecutable,
    string? ApkFile,
    string? DevicePersistentDir,
    int HelperMeasureStart,
    string HelperMode,
    string HelperTopic,
    IReadOnlyList<string> ExtraArgs);
