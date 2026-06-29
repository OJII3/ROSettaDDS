using System.Diagnostics;

namespace ROSettaDDS.UnityPerfHarness
{
    internal sealed class StopwatchPerfClock : IPerfClock
    {
        public IPerfStopwatch Start() => new StopwatchWrapper(Stopwatch.StartNew());
    }
}
