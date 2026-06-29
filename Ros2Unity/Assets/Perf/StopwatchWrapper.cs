using System;
using System.Diagnostics;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class StopwatchWrapper : IPerfStopwatch
    {
        private readonly Stopwatch _stopwatch;

        internal StopwatchWrapper(Stopwatch stopwatch)
        {
            _stopwatch = stopwatch ?? throw new ArgumentNullException(nameof(stopwatch));
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;
        public void Stop() => _stopwatch.Stop();
        public void Dispose() { }
    }
}
