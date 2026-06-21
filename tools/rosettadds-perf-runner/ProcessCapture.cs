using System.Diagnostics;
using System.Text;

namespace ROSettaDDS.PerfRunner;

internal sealed class ProcessCapture : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdoutWriter;
    private readonly StreamWriter _stderrWriter;
    private readonly List<string> _stdoutLines = new();
    private readonly object _gate = new();

    private ProcessCapture(Process process, string stdoutPath, string stderrPath)
    {
        _process = process;
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        _stdoutWriter = new StreamWriter(stdoutPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _stderrWriter = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_gate)
            {
                _stdoutLines.Add(e.Data);
                _stdoutWriter.WriteLine(e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_gate)
            {
                _stderrWriter.WriteLine(e.Data);
            }
        };
    }

    internal int Id => _process.Id;
    internal bool HasExited => _process.HasExited;
    internal StreamWriter StandardInput => _process.StandardInput;

    internal static ProcessCapture Start(
        string fileName,
        IEnumerable<string> arguments,
        string stdoutPath,
        string stderrPath,
        Action<IDictionary<string, string?>>? configureEnvironment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        configureEnvironment?.Invoke(startInfo.Environment);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var capture = new ProcessCapture(process, stdoutPath, stderrPath);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return capture;
    }

    internal async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw new TimeoutException("process timed out: " + _process.StartInfo.FileName);
        }
    }

    internal async Task<Ros2HelperEvent> WaitForEventAsync(string eventName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        int index = 0;
        while (!cts.IsCancellationRequested)
        {
            List<string> snapshot;
            lock (_gate)
            {
                snapshot = new List<string>(_stdoutLines);
            }

            for (; index < snapshot.Count; index++)
            {
                if (!Ros2HelperEvent.TryParse(snapshot[index], out Ros2HelperEvent parsed))
                {
                    continue;
                }
                if (parsed.Event == "error")
                {
                    throw new InvalidOperationException("helper error: " + parsed.Message);
                }
                if (parsed.Event == eventName)
                {
                    return parsed;
                }
            }

            await Task.Delay(20, cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("timed out waiting for event: " + eventName);
    }

    public void Dispose()
    {
        Kill();
        _stdoutWriter.Dispose();
        _stderrWriter.Dispose();
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
        }
    }
}
