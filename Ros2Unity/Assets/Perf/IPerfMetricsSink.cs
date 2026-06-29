using System;
using System.Collections.Generic;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// perf run 中の event / sentinel 出力の抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfMetricsSink : IDisposable
    {
        void Event(string name, IDictionary<string, object> fields = null);
        void WriteSentinel(string path, string content);
    }
}
