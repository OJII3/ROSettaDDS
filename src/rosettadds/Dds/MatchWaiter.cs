using System.Diagnostics;

namespace ROSettaDDS.Dds;

internal static class MatchWaiter
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    public static async Task<bool> WaitUntilMatchedAsync(
        Func<int> currentCountAccessor,
        int minCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (currentCountAccessor is null) throw new ArgumentNullException(nameof(currentCountAccessor));
        if (minCount <= 0) return true;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (currentCountAccessor() >= minCount)
            {
                return true;
            }
            if (sw.Elapsed >= timeout)
            {
                return false;
            }
            try
            {
                await Task.Delay(DefaultPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }
}
