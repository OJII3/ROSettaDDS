using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Dds;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// StatefulWriter が保持する matched reader の集合と PublicationMatchedStatus を管理する。
/// </summary>
internal sealed class MatchedReaderRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ReaderProxy> _matched = new();
    private long _totalMatchedReaders;
    private Guid? _lastSubscriptionHandle;
    private int _lastReportedCurrentReaders;
    private long _lastReportedTotalReaders;

    /// <summary>
    /// remote reader を match する。既存の場合は locator を更新する。
    /// </summary>
    public (ReaderProxy Proxy, bool Added) Match(
        Guid readerGuid,
        Locator? locator,
        ReliabilityKind reliability)
    {
        lock (_lock)
        {
            if (_matched.TryGetValue(readerGuid, out var existing))
            {
                existing.UpdateUnicastLocator(locator);
                return (existing, false);
            }
            else
            {
                var proxy = new ReaderProxy(readerGuid, locator, reliability);
                _matched[readerGuid] = proxy;
                _totalMatchedReaders++;
                _lastSubscriptionHandle = readerGuid;
                return (proxy, true);
            }
        }
    }

    public void Unmatch(Guid readerGuid)
    {
        lock (_lock) { _matched.Remove(readerGuid); }
    }

    public ReaderProxy? Find(Guid readerGuid)
    {
        lock (_lock) { return _matched.TryGetValue(readerGuid, out var p) ? p : null; }
    }

    public ReaderProxy[] Snapshot()
    {
        lock (_lock) { return _matched.Values.ToArray(); }
    }

    public int Count
    {
        get { lock (_lock) { return _matched.Count; } }
    }

    public PublicationMatchedStatus TakePublicationMatchedStatus()
    {
        int current;
        long total;
        int currentChange;
        long totalChange;
        Guid? lastHandle;
        lock (_lock)
        {
            current = _matched.Count;
            total = _totalMatchedReaders;
            lastHandle = _lastSubscriptionHandle;
            currentChange = current - _lastReportedCurrentReaders;
            totalChange = total - _lastReportedTotalReaders;
            _lastReportedCurrentReaders = current;
            _lastReportedTotalReaders = total;
        }
        return new PublicationMatchedStatus
        {
            CurrentCount = current,
            CurrentCountChange = currentChange,
            TotalCount = checked((int)Math.Min(total, int.MaxValue)),
            TotalCountChange = checked((int)Math.Min(totalChange, int.MaxValue)),
            LastSubscriptionHandle = lastHandle,
        };
    }

    /// <summary>
    /// reliable reader のみを対象に、全 reader の最小 acked SN を返す。
    /// reliable reader が 1 つもない場合は null。
    /// </summary>
    public SequenceNumber? MinimumReliableAcknowledged()
    {
        lock (_lock)
        {
            if (_matched.Count == 0) return null;
            long minAcked = long.MaxValue;
            bool hasReliable = false;
            foreach (var proxy in _matched.Values)
            {
                if (!proxy.IsReliable) continue;
                hasReliable = true;
                var acked = proxy.HighestAcked.Value;
                if (acked < minAcked) minAcked = acked;
            }
            if (!hasReliable) return null;
            return new SequenceNumber(minAcked);
        }
    }
}
