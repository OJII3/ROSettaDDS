using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Reader;

/// <summary>
/// Stateful Reader が保持する remote Writer の状態。RTPS 仕様 8.4.10。
/// 受信 SN の追跡と HEARTBEAT 範囲の保持、ACKNACK bitmap 構築を行う。
/// </summary>
public sealed class WriterProxy
{
    private const int MaxBitmapBits = SequenceNumberSet.MaxNumBits;

    private readonly object _lock = new();
    private readonly List<SequenceNumberRange> _satisfiedRanges = new();
    private long _highestContiguous;
    private long _firstAvailable = 1;   // HB.firstSN
    private long _lastAvailable = 0;    // HB.lastSN
    private Locator? _unicastReplyLocator;
    private int _ackNackCount;

    public Guid WriterGuid { get; }

    /// <summary>送信元 Participant のメタトラフィック unicast Locator (ACKNACK 返送先)。</summary>
    public Locator? UnicastReplyLocator
    {
        get { lock (_lock) { return _unicastReplyLocator; } }
    }

    public WriterProxy(Guid writerGuid, Locator? unicastReplyLocator = null)
    {
        WriterGuid = writerGuid;
        _unicastReplyLocator = unicastReplyLocator;
    }

    public void UpdateUnicastReplyLocator(Locator? unicastReplyLocator)
    {
        lock (_lock) { _unicastReplyLocator = unicastReplyLocator; }
    }

    /// <summary>新規受信なら true、重複なら false を返す。</summary>
    public bool MarkReceived(SequenceNumber sn)
    {
        lock (_lock)
        {
            long value = sn.Value;
            if (IsSatisfied(value))
            {
                return false;
            }

            AddSatisfiedRange(value, value);
            return true;
        }
    }

    /// <summary>Writer から GAP として通知された SN を、今後要求しないものとして扱う。</summary>
    public void MarkGap(SequenceNumber gapStart, SequenceNumberSet gapList)
    {
        lock (_lock)
        {
            AddSatisfiedRange(gapStart.Value, gapList.BitmapBase.Value - 1);

            foreach (var sn in gapList.EnumerateSet())
            {
                AddSatisfiedRange(sn.Value, sn.Value);
            }
        }
    }

    /// <summary>HEARTBEAT で通知された SN 範囲を保存する。</summary>
    public void UpdateHeartbeatRange(SequenceNumber firstSn, SequenceNumber lastSn)
    {
        lock (_lock)
        {
            _firstAvailable = firstSn.Value;
            _lastAvailable = lastSn.Value;
            EnsureContiguousAtLeast(_firstAvailable - 1);
        }
    }

    /// <summary>これまでに受信した最大の連続 SN (= bitmapBase - 1)。</summary>
    public SequenceNumber HighestContiguousReceived
    {
        get
        {
            lock (_lock) { return new SequenceNumber(ComputeHighestContiguous()); }
        }
    }

    /// <summary>ACKNACK の単調増加 count。</summary>
    public int IncrementAckNackCount() => Interlocked.Increment(ref _ackNackCount);

    /// <summary>受信済み/GAP 済みとして個別に追跡している範囲数 (テスト/診断用)。</summary>
    internal int TrackedSatisfiedRangeCount
    {
        get { lock (_lock) { return _satisfiedRanges.Count; } }
    }

    /// <summary>
    /// 現在の受信状態から ACKNACK の SequenceNumberSet を構築する。
    /// bitmapBase = 次に期待する SN (これ未満は暗黙的に ack 済み)。
    /// bitmap の bit i が 1 のとき (bitmapBase + i) が欠損で再送要求。
    /// </summary>
    public SequenceNumberSet BuildAckNackBitmap()
    {
        lock (_lock)
        {
            long base_ = ComputeHighestContiguous() + 1;
            // base_ が _lastAvailable を超えたら numBits=0 (全て ack)
            if (base_ > _lastAvailable)
            {
                return new SequenceNumberSet(new SequenceNumber(base_), 0, Array.Empty<uint>());
            }
            long span = _lastAvailable - base_ + 1;
            int numBits = span > MaxBitmapBits ? MaxBitmapBits : (int)span;
            int wordCount = (numBits + 31) / 32;
            var bitmap = new uint[wordCount];
            int rangeIndex = 0;
            for (int i = 0; i < numBits; i++)
            {
                long sn = base_ + i;
                while (rangeIndex < _satisfiedRanges.Count && _satisfiedRanges[rangeIndex].End < sn)
                {
                    rangeIndex++;
                }

                bool isSatisfied = rangeIndex < _satisfiedRanges.Count
                    && _satisfiedRanges[rangeIndex].Contains(sn);
                if (!isSatisfied)
                {
                    int word = i / 32;
                    int bitInWord = i % 32;
                    bitmap[word] |= 1u << (31 - bitInWord);
                }
            }
            return new SequenceNumberSet(new SequenceNumber(base_), numBits, bitmap);
        }
    }

    /// <summary>このプロキシで欠損とみなしている SN のリスト (テスト/診断用)。</summary>
    public IReadOnlyList<SequenceNumber> MissingSequenceNumbers()
    {
        lock (_lock)
        {
            long base_ = ComputeHighestContiguous() + 1;
            var result = new List<SequenceNumber>();
            int rangeIndex = 0;
            for (long sn = Math.Max(base_, _firstAvailable); sn <= _lastAvailable; sn++)
            {
                while (rangeIndex < _satisfiedRanges.Count && _satisfiedRanges[rangeIndex].End < sn)
                {
                    rangeIndex++;
                }

                bool isSatisfied = rangeIndex < _satisfiedRanges.Count
                    && _satisfiedRanges[rangeIndex].Contains(sn);
                if (!isSatisfied)
                {
                    result.Add(new SequenceNumber(sn));
                }
            }
            return result;
        }
    }

    private long ComputeHighestContiguous()
    {
        EnsureContiguousAtLeast(_firstAvailable - 1);
        return _highestContiguous;
    }

    private bool IsSatisfied(long sn)
    {
        if (sn <= _highestContiguous)
        {
            return true;
        }

        for (int i = 0; i < _satisfiedRanges.Count; i++)
        {
            var range = _satisfiedRanges[i];
            if (sn < range.Start)
            {
                return false;
            }

            if (range.Contains(sn))
            {
                return true;
            }
        }

        return false;
    }

    private void AddSatisfiedRange(long start, long end)
    {
        if (end < start || end <= _highestContiguous)
        {
            return;
        }

        if (start <= _highestContiguous)
        {
            start = _highestContiguous + 1;
        }

        int index = 0;
        while (index < _satisfiedRanges.Count && IsBeforeWithGap(_satisfiedRanges[index].End, start))
        {
            index++;
        }

        long mergedStart = start;
        long mergedEnd = end;
        while (index < _satisfiedRanges.Count && !IsBeforeWithGap(mergedEnd, _satisfiedRanges[index].Start))
        {
            var range = _satisfiedRanges[index];
            mergedStart = Math.Min(mergedStart, range.Start);
            mergedEnd = Math.Max(mergedEnd, range.End);
            _satisfiedRanges.RemoveAt(index);
        }

        _satisfiedRanges.Insert(index, new SequenceNumberRange(mergedStart, mergedEnd));
        AdvanceHighestContiguous();
    }

    private void EnsureContiguousAtLeast(long value)
    {
        if (_highestContiguous < value)
        {
            _highestContiguous = value;
        }

        AdvanceHighestContiguous();
    }

    private void AdvanceHighestContiguous()
    {
        while (_satisfiedRanges.Count > 0 && !IsBeforeWithGap(_highestContiguous, _satisfiedRanges[0].Start))
        {
            var range = _satisfiedRanges[0];
            if (_highestContiguous < range.End)
            {
                _highestContiguous = range.End;
            }

            _satisfiedRanges.RemoveAt(0);
        }
    }

    private static bool IsBeforeWithGap(long leftEnd, long rightStart)
    {
        return leftEnd < rightStart && (leftEnd == long.MaxValue || leftEnd + 1 < rightStart);
    }

    private readonly struct SequenceNumberRange
    {
        public SequenceNumberRange(long start, long end)
        {
            Start = start;
            End = end;
        }

        public long Start { get; }
        public long End { get; }

        public bool Contains(long value) => value >= Start && value <= End;
    }
}
