using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeProcessDriver : IProcessDriver
{
    public List<LaunchSpec> StartCalls { get; } = new();
    public List<string> WaitForSentinelCalls { get; } = new();
    public List<TimeSpan> WaitForExitCalls { get; } = new();
    public List<(string Remote, string Local)> CopyFileCalls { get; } = new();
    public List<(string Local, string Remote)> PushFileCalls { get; } = new();
    public int KillCalls { get; private set; }

    public Func<LaunchSpec, CancellationToken, Task>? StartImpl { get; set; }
    public Func<string, TimeSpan, CancellationToken, Task<bool>>? WaitForSentinelImpl { get; set; }
    public Func<TimeSpan, CancellationToken, Task<int>>? WaitForExitImpl { get; set; }
    public Action? KillImpl { get; set; }
    public Func<LogKind, CancellationToken, Stream>? OpenLogImpl { get; set; }
    public Func<string, string, CancellationToken, Task>? CopyFileImpl { get; set; }
    public Func<string, string, CancellationToken, Task>? PushFileImpl { get; set; }

    public Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        StartCalls.Add(spec);
        return StartImpl?.Invoke(spec, ct) ?? Task.CompletedTask;
    }

    public Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        WaitForSentinelCalls.Add(name);
        return WaitForSentinelImpl?.Invoke(name, timeout, ct) ?? Task.FromResult(true);
    }

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        WaitForExitCalls.Add(timeout);
        return WaitForExitImpl?.Invoke(timeout, ct) ?? Task.FromResult(0);
    }

    public void Kill()
    {
        KillCalls++;
        KillImpl?.Invoke();
    }

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
        => OpenLogImpl?.Invoke(kind, ct) ?? new MemoryStream();

    public Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        CopyFileCalls.Add((remoteName, localPath));
        return CopyFileImpl?.Invoke(remoteName, localPath, ct) ?? Task.CompletedTask;
    }

    public Task PushFileAsync(string localPath, string remoteName, CancellationToken ct)
    {
        PushFileCalls.Add((localPath, remoteName));
        return PushFileImpl?.Invoke(localPath, remoteName, ct) ?? Task.CompletedTask;
    }

    public void Dispose() { }
}
