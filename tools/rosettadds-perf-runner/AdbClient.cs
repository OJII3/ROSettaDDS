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

    public string Serial => _serial;

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

    public async Task<AdbResult> ForceStopAsync(string packageId, CancellationToken ct)
    {
        var r = await RunAsync($"adb -s {_serial} shell am force-stop {packageId}", ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"adb force-stop failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
        return r;
    }

    public async Task<AdbResult> StartActivityAsync(
        string packageId,
        string activityComponent,
        IReadOnlyList<string> playerArgs,
        CancellationToken ct)
    {
        string joined = string.Join(" ", playerArgs);
        var r = await RunAsync(
            $"adb -s {_serial} shell 'am start -W -n {packageId}/{activityComponent} " +
             $"--es unity \"{joined}\"'",
            ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"adb start-activity failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
        return r;
    }

    public Task<AdbResult> PullFileAsync(string remotePath, string localPath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} pull {remotePath} {localPath}", ct);

    public Task<AdbResult> PushFileAsync(string localPath, string remotePath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} push {localPath} {remotePath}", ct);
}

internal sealed class RealAdbCommandSink : IAdbCommandSink
{
    private readonly string _adbPath;

    public RealAdbCommandSink(string adbPath = "adb")
    {
        _adbPath = adbPath;
    }

    public async Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        string resolved = command.StartsWith("adb ", StringComparison.Ordinal)
            ? _adbPath + command.Substring(3)
            : command;
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(resolved);
        using var p = Process.Start(psi)!;
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new AdbResult(p.ExitCode, stdout, stderr);
    }
}
