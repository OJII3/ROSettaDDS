using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal readonly record struct AdbResult(int ExitCode, string Stdout, string Stderr);

internal interface IAdbCommandSink
{
    Task<AdbResult> RunAsync(string command, CancellationToken ct);
}

internal sealed class AdbClient : IAdbCommandSink
{
    private readonly IAdbCommandSink _sink;
    private readonly string _serial;

    public AdbClient(IAdbCommandSink sink, string serial)
    {
        _sink = sink;
        _serial = serial;
    }

    public Task<AdbResult> RunAsync(string command, CancellationToken ct)
        => _sink.RunAsync(command, ct);

    public async Task<AdbResult> InstallApkAsync(string apkPath, CancellationToken ct)
    {
        var r = await RunAsync($"adb -s {_serial} install -r {apkPath}", ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"adb install failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
        return r;
    }

    public Task<AdbResult> ForceStopAsync(string packageId, CancellationToken ct)
        => RunAsync($"adb -s {_serial} shell am force-stop {packageId}", ct);

    public Task<AdbResult> StartActivityAsync(
        string packageId,
        string activityComponent,
        IReadOnlyList<string> playerArgs,
        CancellationToken ct)
    {
        string joined = string.Join(" ", playerArgs);
        return RunAsync(
            $"adb -s {_serial} shell am start -W -n {packageId}/{activityComponent} " +
            $"--es args \"{joined}\"",
            ct);
    }

    public Task<AdbResult> PullFileAsync(string remotePath, string localPath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} pull {remotePath} {localPath}", ct);
}

internal sealed class RealAdbCommandSink : IAdbCommandSink
{
    public async Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var p = Process.Start(psi)!;
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new AdbResult(p.ExitCode, stdout, stderr);
    }
}
