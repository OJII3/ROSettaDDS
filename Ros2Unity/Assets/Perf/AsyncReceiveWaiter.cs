using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ROSettaDDS.UnityPerfHarness
{
    internal static class AsyncReceiveWaiter
    {
        internal static Task<bool> WaitUntilAsync(Func<bool> isComplete, TimeSpan timeout)
            => WaitUntilAsync(isComplete, timeout, Task.Delay);

        internal static async Task<bool> WaitUntilAsync(
            Func<bool> isComplete,
            TimeSpan timeout,
            Func<TimeSpan, Task> delayAsync)
        {
            var deadline = Stopwatch.StartNew();
            while (!isComplete())
            {
                TimeSpan remaining = timeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                TimeSpan delay = remaining < TimeSpan.FromMilliseconds(1)
                    ? remaining
                    : TimeSpan.FromMilliseconds(1);
                await delayAsync(delay);
            }

            return true;
        }
    }
}
