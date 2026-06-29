using System;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// Stopwatch の抽象。single-thread 想定。
    /// Dispose は using 構文で使うため。実装 (StopwatchWrapper) は
    /// 内部の System.Diagnostics.Stopwatch を所有しないため no-op。
    /// </summary>
    internal interface IPerfStopwatch : IDisposable
    {
        TimeSpan Elapsed { get; }
        void Stop();
    }
}
