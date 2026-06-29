using System;
using System.Collections.Generic;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityVerification.Tests
{
    /// <summary>
    /// EditMode テスト用の IPerfMetricsSink fake。
    /// 出力された event と sentinel を記録する。
    /// </summary>
    internal sealed class FakePerfMetricsSink : IPerfMetricsSink
    {
        public List<(string Name, IDictionary<string, object> Fields)> Events { get; } = new();
        public List<(string Path, string Content)> Sentinels { get; } = new();

        public void Event(string name, IDictionary<string, object> fields = null)
        {
            Events.Add((name, fields ?? new Dictionary<string, object>()));
        }

        public void WriteSentinel(string path, string content)
        {
            Sentinels.Add((path, content));
        }

        public void Dispose() { }
    }

    /// <summary>
    /// EditMode テスト用の IPerfProfilerSampler fake。
    /// 設定可能な Snapshot 戻り値を持つ。
    /// </summary>
    internal sealed class FakePerfProfilerSampler : IPerfProfilerSampler
    {
        public int CollectCallCount { get; private set; }
        public IDictionary<string, object> NextSnapshot { get; set; }
            = new Dictionary<string, object>();

        public void Collect() => CollectCallCount++;

        public IDictionary<string, object> Snapshot() => NextSnapshot;
        public void Dispose() { }
    }

    /// <summary>
    /// EditMode テスト用の IPerfClock fake。設定可能な Elapsed 値を返す。
    /// </summary>
    internal sealed class FakePerfClock : IPerfClock
    {
        public TimeSpan Elapsed { get; set; }
        public IPerfStopwatch Start() => new FakePerfStopwatch(this);
    }

    /// <summary>
    /// EditMode テスト用の IPerfStopwatch fake。
    /// </summary>
    internal sealed class FakePerfStopwatch : IPerfStopwatch
    {
        private readonly FakePerfClock _clock;
        public FakePerfStopwatch(FakePerfClock clock) { _clock = clock; }
        public TimeSpan Elapsed => _clock.Elapsed;
        public void Stop() { }
        public void Dispose() { }
    }
}
