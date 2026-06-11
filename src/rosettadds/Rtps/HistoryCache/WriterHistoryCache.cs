using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.HistoryCache;

/// <summary>
/// Writer 側の履歴キャッシュ。SequenceNumber を 1 から自動採番し、
/// (Reliable で) Reader からの再送要求に応えるためにサンプルを保持する。
/// KEEP_ALL 相当 (明示削除のみ)。<see cref="MaxSamples"/> を超えると古い順に自動削除される。
/// </summary>
public sealed class WriterHistoryCache
{
    private readonly object _lock = new();
    private readonly SortedDictionary<long, CacheChange> _changes = new();
    private long _lastSequence;
    private readonly Guid _writerGuid;

    /// <summary>保持できる最大サンプル数。0 以下なら無制限。</summary>
    public int MaxSamples { get; }

    public Guid WriterGuid => _writerGuid;

    public WriterHistoryCache(Guid writerGuid, int maxSamples = 0)
    {
        _writerGuid = writerGuid;
        MaxSamples = maxSamples;
        _lastSequence = 0;
    }

    /// <summary>新規サンプルを追加し、採番した <see cref="CacheChange"/> を返す。</summary>
    public CacheChange Add(ChangeKind kind, ReadOnlyMemory<byte> payload, Time sourceTimestamp)
    {
        lock (_lock)
        {
            _lastSequence++;
            var sn = new SequenceNumber(_lastSequence);
            var change = new CacheChange(kind, _writerGuid, sn, sourceTimestamp, payload);
            _changes[_lastSequence] = change;
            EvictIfNeeded();
            return change;
        }
    }

    /// <summary>指定 SN のサンプルを取得する (再送用)。なければ null。</summary>
    public CacheChange? Get(SequenceNumber sn)
    {
        lock (_lock)
        {
            return _changes.TryGetValue(sn.Value, out var change) ? change : null;
        }
    }

    /// <summary>現在保持している最小 SN。空なら 0。</summary>
    public SequenceNumber FirstSequenceNumber
    {
        get
        {
            lock (_lock)
            {
                return _changes.Count == 0
                    ? new SequenceNumber(0L)
                    : new SequenceNumber(_changes.Keys.First());
            }
        }
    }

    /// <summary>これまでに採番した最大 SN (= 累積発行数)。</summary>
    public SequenceNumber LastSequenceNumber
    {
        get { lock (_lock) { return new SequenceNumber(_lastSequence); } }
    }

    /// <summary>現在保持しているサンプル数。</summary>
    public int Count
    {
        get { lock (_lock) { return _changes.Count; } }
    }

    /// <summary>指定範囲 [min, max] (両端含む) のサンプルを SN 順に列挙。</summary>
    public IReadOnlyList<CacheChange> EnumerateRange(SequenceNumber min, SequenceNumber max)
    {
        lock (_lock)
        {
            var result = new List<CacheChange>();
            foreach (var (key, change) in _changes)
            {
                if (key < min.Value) continue;
                if (key > max.Value) break;
                result.Add(change);
            }
            return result;
        }
    }

    /// <summary>指定 SN 以下のサンプルを破棄する (acked された分の解放)。</summary>
    public void RemoveBelowOrEqual(SequenceNumber sn)
    {
        lock (_lock)
        {
            var keysToRemove = _changes.Keys.Where(k => k <= sn.Value).ToArray();
            foreach (var k in keysToRemove)
            {
                _changes.Remove(k);
            }
        }
    }

    /// <summary>指定 SN のサンプルを破棄する。存在して削除できた場合は true。</summary>
    public bool Remove(SequenceNumber sn)
    {
        lock (_lock)
        {
            return _changes.Remove(sn.Value);
        }
    }

    private void EvictIfNeeded()
    {
        if (MaxSamples <= 0) return;
        while (_changes.Count > MaxSamples)
        {
            var firstKey = _changes.Keys.First();
            _changes.Remove(firstKey);
        }
    }
}
