using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class SedpEndpointAdvertiserTests
{
    [Fact]
    public async Task RunAsync_は_cancellation済みOperationCanceledExceptionを抑制する()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var advertiser = new SedpEndpointAdvertiser(NullLogger.Instance, () => cts.Token);

        var act = async () => await advertiser.RunAsync(
            _ => throw new OperationCanceledException(cts.Token),
            "failed");

        await act.Should().NotThrowAsync();
    }
}
