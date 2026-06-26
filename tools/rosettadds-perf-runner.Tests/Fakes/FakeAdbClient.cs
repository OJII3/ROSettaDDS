using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeAdbClient : IAdbCommandSink
{
    public List<string> Calls { get; } = new();
    public int ExitCodeOverride { get; set; } = 0;
    public string StderrOverride { get; set; } = string.Empty;
    public Queue<int>? ScriptedExitCodes { get; set; }
    public Action<string, string>? FileProvider { get; set; }

    public Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        Calls.Add(command);
        int code;
        if (ScriptedExitCodes is { } q && q.Count > 0)
        {
            code = q.Dequeue();
        }
        else
        {
            code = ExitCodeOverride;
        }
        if (command.Contains("pull ", StringComparison.Ordinal) && code == 0 && FileProvider is { } fp)
        {
            string[] parts = command.Split("pull ", 2)[1].Split(' ', 2);
            fp(parts[0], parts[1]);
        }
        return Task.FromResult(new AdbResult(code, string.Empty, StderrOverride));
    }
}
