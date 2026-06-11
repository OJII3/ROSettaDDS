namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// CacheChange の種別。RTPS 仕様 8.2.5。
/// 通常のデータ更新は <see cref="Alive"/>、Dispose / Unregister 通知は他のキンド。
/// </summary>
public enum ChangeKind
{
    Alive = 0,
    NotAliveDisposed = 1,
    NotAliveUnregistered = 2,
    NotAliveDisposedUnregistered = 3,
}
