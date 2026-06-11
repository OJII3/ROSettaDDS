using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;

namespace ROSettaDDS.Rtps;

/// <summary>
/// RTPS 8.3.4 の Receiver 状態。
/// INFO_DST / INFO_TS によって submessage 間で引き継がれる。
/// </summary>
public readonly struct RtpsReceiverContext
{
    public GuidPrefix SourceGuidPrefix { get; }
    public GuidPrefix DestGuidPrefix { get; }
    public ProtocolVersion Version { get; }
    public VendorId VendorId { get; }
    public Time? Timestamp { get; }

    public RtpsReceiverContext(
        GuidPrefix sourceGuidPrefix,
        GuidPrefix destGuidPrefix,
        ProtocolVersion version,
        VendorId vendorId,
        Time? timestamp)
    {
        SourceGuidPrefix = sourceGuidPrefix;
        DestGuidPrefix = destGuidPrefix;
        Version = version;
        VendorId = vendorId;
        Timestamp = timestamp;
    }

    public RtpsReceiverContext WithDest(GuidPrefix dest)
        => new(SourceGuidPrefix, dest, Version, VendorId, Timestamp);

    public RtpsReceiverContext WithTimestamp(Time? ts)
        => new(SourceGuidPrefix, DestGuidPrefix, Version, VendorId, ts);
}

/// <summary>
/// RTPS endpoint が実装するインタフェース。
/// <para>
/// netstandard2.1 / Unity IL2CPP 対応のため default interface method は使わない。
/// 不要なメソッドは no-op で実装すること。
/// </para>
/// </summary>
public interface IRtpsSubmessageHandler
{
    /// <param name="endianness">submessage の endianness (E フラグ)。InlineQos 解釈に使用。</param>
    void OnData(in RtpsReceiverContext ctx, DataSubmessage data, CdrEndianness endianness);
    /// <param name="endianness">submessage の endianness (E フラグ)。InlineQos 解釈に使用。</param>
    void OnDataFrag(in RtpsReceiverContext ctx, DataFragSubmessage dataFrag, CdrEndianness endianness);
    void OnHeartbeat(in RtpsReceiverContext ctx, HeartbeatSubmessage hb);
    void OnAckNack(in RtpsReceiverContext ctx, AckNackSubmessage ack);
    void OnGap(in RtpsReceiverContext ctx, GapSubmessage gap);
}

/// <summary>
/// RTPS メッセージを走査し、INFO_DST / INFO_TS の Receiver 状態を維持しながら
/// submessage を <see cref="IRtpsSubmessageHandler"/> へ転送する共通ディスパッチャ。
/// </summary>
public static class RtpsMessageDispatcher
{
    /// <summary>
    /// パケットを RTPS メッセージとして解釈し、各 submessage を handler へ dispatch する。
    /// </summary>
    /// <param name="packet">受信 RTPS パケット。</param>
    /// <param name="localPrefix">この endpoint の GuidPrefix (INFO_DST フィルタに使用)。</param>
    /// <param name="handler">submessage ハンドラ。</param>
    public static void Dispatch(
        ReadOnlyMemory<byte> packet,
        GuidPrefix localPrefix,
        IRtpsSubmessageHandler handler)
    {
        if (!RtpsHeader.TryRead(packet.Span, out var version, out var vendorId, out var sourcePrefix))
        {
            return;
        }

        var ctx = new RtpsReceiverContext(
            sourceGuidPrefix: sourcePrefix,
            destGuidPrefix: GuidPrefix.Unknown,
            version: version,
            vendorId: vendorId,
            timestamp: null);

        var reader = RtpsMessageReader.FromMemory(packet);
        while (reader.TryReadNextMemory(out var hdr, out var body))
        {
            switch (hdr.Kind)
            {
                case SubmessageKind.InfoDestination:
                    {
                        var infoDst = InfoDestinationSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                        ctx = ctx.WithDest(infoDst.GuidPrefix);
                        break;
                    }
                case SubmessageKind.InfoTimestamp:
                    {
                        var infoTs = InfoTimestampSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                        ctx = ctx.WithTimestamp(infoTs.Invalidate ? null : infoTs.Timestamp);
                        break;
                    }
                case SubmessageKind.Data:
                case SubmessageKind.DataFrag:
                case SubmessageKind.Heartbeat:
                case SubmessageKind.AckNack:
                case SubmessageKind.Gap:
                    {
                        // INFO_DST 宛先フィルタ: Unknown でなく localPrefix と異なる場合はスキップ
                        var dest = ctx.DestGuidPrefix;
                        if (!dest.Equals(GuidPrefix.Unknown) && !dest.Equals(localPrefix))
                        {
                            break;
                        }

                        switch (hdr.Kind)
                        {
                            case SubmessageKind.Data:
                                {
                                    var data = DataSubmessage.ReadBodyBorrowed(body, hdr.Endianness, hdr.Flags);
                                    handler.OnData(in ctx, data, hdr.Endianness);
                                    break;
                                }
                            case SubmessageKind.DataFrag:
                                {
                                    var dataFrag = DataFragSubmessage.ReadBodyBorrowed(body, hdr.Endianness, hdr.Flags);
                                    handler.OnDataFrag(in ctx, dataFrag, hdr.Endianness);
                                    break;
                                }
                            case SubmessageKind.Heartbeat:
                                {
                                    var hb = HeartbeatSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                                    handler.OnHeartbeat(in ctx, hb);
                                    break;
                                }
                            case SubmessageKind.AckNack:
                                {
                                    var ack = AckNackSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                                    handler.OnAckNack(in ctx, ack);
                                    break;
                                }
                            case SubmessageKind.Gap:
                                {
                                    var gap = GapSubmessage.ReadBody(body.Span, hdr.Endianness, hdr.Flags);
                                    handler.OnGap(in ctx, gap);
                                    break;
                                }
                        }
                        break;
                    }
                default:
                    // Pad, InfoSource, InfoReply 等は無視
                    break;
            }
        }
    }
}
