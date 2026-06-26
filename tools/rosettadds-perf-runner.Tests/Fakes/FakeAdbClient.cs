using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeAdbClient : IAdbCommandSink
{
    public List<string> Calls { get; } = new();

    public Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        Calls.Add(command);
        return Task.FromResult(new AdbResult(0, string.Empty, string.Empty));
    }
}
