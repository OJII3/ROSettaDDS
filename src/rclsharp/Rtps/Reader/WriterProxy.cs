using Rclsharp.Common;
using Rclsharp.Rtps.Submessages;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Rtps.Reader;

/// <summary>
/// Stateful Reader が保持する remote Writer の状態。RTPS 仕様 8.4.10。
/// 受信 SN の追跡と HEARTBEAT 範囲の保持、ACKNACK bitmap 構築を行う。
/// </summary>
public sealed class WriterProxy
{
    private const int MaxBitmapBits = SequenceNumberSet.MaxNumBits;

    private readonly object _lock = new();
    private readonly HashSet<long> _received = new();
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
        lock (_lock) { return _received.Add(sn.Value); }
    }

    /// <summary>Writer から GAP として通知された SN を、今後要求しないものとして扱う。</summary>
    public void MarkGap(SequenceNumber gapStart, SequenceNumberSet gapList)
    {
        lock (_lock)
        {
            for (long sn = gapStart.Value; sn < gapList.BitmapBase.Value; sn++)
            {
                _received.Add(sn);
            }
            foreach (var sn in gapList.EnumerateSet())
            {
                _received.Add(sn.Value);
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
            int span = (int)(_lastAvailable - base_ + 1);
            int numBits = Math.Min(span, MaxBitmapBits);
            int wordCount = (numBits + 31) / 32;
            var bitmap = new uint[wordCount];
            for (int i = 0; i < numBits; i++)
            {
                long sn = base_ + i;
                if (!_received.Contains(sn))
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
            for (long sn = Math.Max(base_, _firstAvailable); sn <= _lastAvailable; sn++)
            {
                if (!_received.Contains(sn))
                {
                    result.Add(new SequenceNumber(sn));
                }
            }
            return result;
        }
    }

    private long ComputeHighestContiguous()
    {
        // _firstAvailable - 1 から始めて、連続して受信している SN を探す
        long high = _firstAvailable - 1;
        while (_received.Contains(high + 1))
        {
            high++;
        }
        return high;
    }
}
