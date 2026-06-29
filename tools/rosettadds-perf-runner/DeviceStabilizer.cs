using System;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal interface IDeviceStabilizer
{
    Task StabilizeAsync(TimeSpan timeout, CancellationToken ct);
}

internal sealed class AndroidDeviceStabilizer : IDeviceStabilizer
{
    private readonly AdbClient _adb;
    private readonly string _hostForPing;

    public AndroidDeviceStabilizer(AdbClient adb, string hostForPing)
    {
        _adb = adb;
        _hostForPing = hostForPing;
    }

    public async Task StabilizeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var r1 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc power stayon true", ct);
        if (r1.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc power stayon failed (exit={r1.ExitCode}): {r1.Stderr.Trim()}");
        }

        var r2 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc wifi disable", ct);
        if (r2.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc wifi disable failed (exit={r2.ExitCode}): {r2.Stderr.Trim()}");
        }
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var r3 = await _adb.RunAsync(
            $"adb -s {_adb.Serial} shell svc wifi enable", ct);
        if (r3.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svc wifi enable failed (exit={r3.ExitCode}): {r3.Stderr.Trim()}");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string pingCmd = $"adb -s {_adb.Serial} shell ping -c 5 -W 2 {_hostForPing}";
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var rp = await _adb.RunAsync(pingCmd, ct);
            if (rp.ExitCode == 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new TimeoutException(
            $"timed out waiting for ping {_hostForPing} to succeed within {timeout}");
    }
}

internal sealed class DesktopDeviceStabilizer : IDeviceStabilizer
{
    public Task StabilizeAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
}
