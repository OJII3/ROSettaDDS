using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// ProfilerRecorder のラッパ抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfProfilerSampler : IDisposable
    {
        void Collect();
        IDictionary<string, object> Snapshot();
    }
}
