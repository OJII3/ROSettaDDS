using System;

namespace ROSettaDDS.UnityPerfHarness
{
    /// <summary>
    /// 経過時間計測の抽象。テストでは fake に差し替え可能。
    /// </summary>
    internal interface IPerfClock
    {
        IPerfStopwatch Start();
    }
}
