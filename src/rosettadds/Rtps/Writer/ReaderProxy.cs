using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Writer;

/// <summary>
/// Stateful Writer が保持する remote Reader の状態。RTPS 仕様 8.4.7。
/// reader が ack した SN と再送要求された SN を追跡。
/// </summary>
public sealed class ReaderProxy
{
    private readonly object _lock = new();
    private long _highestAcked;             // ACKNACK.bitmapBase - 1 (これ以下は ack 済み)
    private readonly HashSet<long> _requested = new();   // 明示的に要求された SN 一覧
    private long _lowWatermark;
    private bool _lowWatermarkSet;
    private Locator? _unicastLocator;
    private int _heartbeatCount;

    public Guid ReaderGuid { get; }

    /// <summary>この reader の Reliability 種別。BestEffort reader は ACKNACK を送らない。</summary>
    public ReliabilityKind Reliability { get; }

    /// <summary>この reader が Reliable かどうか。</summary>
    public bool IsReliable => Reliability == ReliabilityKind.Reliable;

    /// <summary>DATA / HEARTBEAT 送信先 unicast Locator (なければ multicast にフォールバック)。</summary>
    public Locator? UnicastLocator
    {
        get { lock (_lock) { return _unicastLocator; } }
    }

    public ReaderProxy(Guid readerGuid, Locator? unicastLocator = null, ReliabilityKind reliability = ReliabilityKind.Reliable)
    {
        ReaderGuid = readerGuid;
        _unicastLocator = unicastLocator;
        _highestAcked = 0;
        Reliability = reliability;
    }

    public void UpdateUnicastLocator(Locator? unicastLocator)
    {
        lock (_lock) { _unicastLocator = unicastLocator; }
    }

    /// <summary>これまで ack 済みの最大 SN。</summary>
    public SequenceNumber HighestAcked
    {
        get { lock (_lock) { return new SequenceNumber(_highestAcked); } }
    }

    /// <summary>HEARTBEAT submessage の単調増加 count。</summary>
    public int IncrementHeartbeatCount() => Interlocked.Increment(ref _heartbeatCount);

    /// <summary>
    /// reader からの ACKNACK を処理する。
    /// bitmapBase 未満は ack 済みとみなし、bitmap 内の set bit を再送要求として記録する。
    /// </summary>
    public void ProcessAckNack(SequenceNumberSet snSet)
    {
        lock (_lock)
        {
            long newAcked = snSet.BitmapBase.Value - 1;
            if (newAcked > _highestAcked)
            {
                _highestAcked = newAcked;
            }
            // ack 済み範囲の requested は破棄
            _requested.RemoveWhere(sn => sn <= _highestAcked);
            // bitmap の set bit を requested に追加
            for (int i = 0; i < snSet.NumBits; i++)
            {
                if (snSet.IsSet(i))
                {
                    long sn = snSet.BitmapBase.Value + i;
                    _requested.Add(sn);
                }
            }
        }
    }

    /// <summary>再送要求 SN の現在のスナップショット (昇順)。</summary>
    public IReadOnlyList<SequenceNumber> RequestedSequenceNumbers()
    {
        lock (_lock)
        {
            return _requested.OrderBy(s => s).Select(s => new SequenceNumber(s)).ToArray();
        }
    }

    /// <summary>指定 SN を要求済みリストから取り除く (再送送出後に呼ぶ)。</summary>
    public void ClearRequested(SequenceNumber sn)
    {
        lock (_lock) { _requested.Remove(sn.Value); }
    }

    /// <summary>
    /// 初回 match 時点で writer 履歴の <c>LastSequenceNumber</c> を記録する。
    /// NACK されてきた SN がこの値以下なら「pre-join サンプル (late-join reader に無関連)」として
    /// 取り扱う。Volatile writer + reliable reader のときに writer 側で設定する。
    /// </summary>
    public bool IsLowWatermarkSet
    {
        get { lock (_lock) { return _lowWatermarkSet; } }
    }

    /// <summary>low watermark (未設定時は <c>null</c>)。</summary>
    public SequenceNumber? LowWatermark
    {
        get { lock (_lock) { return _lowWatermarkSet ? new SequenceNumber(_lowWatermark) : null; } }
    }

    /// <summary>
    /// low watermark を設定する。既に設定済みの呼び出しは最初の値を維持する (idempotent)。
    /// </summary>
    public void SetLowWatermark(SequenceNumber sn)
    {
        lock (_lock)
        {
            if (_lowWatermarkSet)
            {
                return;
            }
            _lowWatermark = sn.Value;
            _lowWatermarkSet = true;
        }
    }

    /// <summary>
    /// 指定 SN が pre-join サンプル (low watermark 以下で「無関連」) かどうか。
    /// 未設定、または SN が 0 (未初期化) のときは <c>false</c>。
    /// </summary>
    public bool IsPreJoin(SequenceNumber sn)
    {
        lock (_lock)
        {
            if (!_lowWatermarkSet)
            {
                return false;
            }
            if (sn.Value <= 0)
            {
                return false;
            }
            return sn.Value <= _lowWatermark;
        }
    }
}
