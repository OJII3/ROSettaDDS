using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal sealed class DesktopProcessDriver : IProcessDriver
{
#pragma warning disable CS0649
    private ProcessCapture? _capture;
#pragma warning restore CS0649
    private bool _disposed;

    public Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        throw new NotImplementedException(
            "DesktopProcessDriver is not wired into Program.RunScenario yet; " +
            "see Task 13.");
    }

    public Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_capture is null) return Task.FromResult(-1);
        return _capture.WaitForExitAsync(timeout);
    }

    public void Kill()
    {
        // _capture is always null until Task 13 wires StartImpl.
    }

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
        => throw new NotImplementedException();

    public Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture?.Dispose();
    }
}
