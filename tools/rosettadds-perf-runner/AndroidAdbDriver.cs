using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal sealed class AndroidAdbDriver : IProcessDriver
{
    private readonly AdbClient _adb;
    private readonly string _packageId;
    private readonly string _activityComponent;
    private readonly string _devicePersistentDir;
    private readonly string _localArtifactDir;
    private bool _disposed;

    public AndroidAdbDriver(
        AdbClient adb,
        string packageId,
        string activityComponent,
        string devicePersistentDir,
        string localArtifactDir)
    {
        _adb = adb;
        _packageId = packageId;
        _activityComponent = activityComponent;
        _devicePersistentDir = devicePersistentDir;
        _localArtifactDir = localArtifactDir;
    }

    public async Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        if (spec.ApkFile is null)
        {
            throw new InvalidOperationException("AndroidAdbDriver requires spec.ApkFile");
        }
        await _adb.InstallApkAsync(spec.ApkFile, ct);
        await _adb.ForceStopAsync(_packageId, ct);
        var args = new List<string>(spec.ExtraArgs);
        await _adb.StartActivityAsync(_packageId, _activityComponent, args, ct);
    }

    public async Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        string remote = _devicePersistentDir + "/" + name;
        string local = Path.Combine(_localArtifactDir, name);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _adb.PullFileAsync(remote, local, ct);
            if (r.ExitCode == 0)
            {
                return true;
            }
            if (r.ExitCode != 1)
            {
                throw new IOException(
                    $"adb pull failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
            }
            await Task.Delay(100, ct);
        }
        return false;
    }

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        return Task.FromResult(0);
    }

    public void Kill()
    {
        _adb.ForceStopAsync(_packageId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
    {
        throw new NotSupportedException(
            "AndroidAdbDriver.OpenLogAsync: logcat streaming is owned by Program.RunScenario, not this driver");
    }

    public async Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        string remote = _devicePersistentDir + "/" + remoteName;
        var r = await _adb.PullFileAsync(remote, localPath, ct);
        if (r.ExitCode != 0)
        {
            throw new IOException(
                $"adb pull failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
    }

    public async Task PushFileAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var r = await _adb.PushFileAsync(localPath, remotePath, ct);
        if (r.ExitCode != 0)
        {
            throw new IOException(
                $"adb push failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
