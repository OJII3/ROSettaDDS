namespace ROSettaDDS.PerfRunner;

internal interface IProcessDriver : IDisposable
{
    Task StartAsync(LaunchSpec spec, CancellationToken ct);
    Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct);
    Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
    void Kill();
    Stream OpenLogAsync(LogKind kind, CancellationToken ct);
    Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct);
}
