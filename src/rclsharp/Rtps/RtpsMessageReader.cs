using Rclsharp.Common;
using Rclsharp.Rtps.Submessages;

namespace Rclsharp.Rtps;

/// <summary>
/// RTPS Message リーダ。ヘッダを検証し、Submessage を順次走査する。
/// length=0 の Submessage はメッセージ末尾までを意味し、その後の MoveNext は false を返す。
/// </summary>
public ref struct RtpsMessageReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public ProtocolVersion Version { get; }
    public VendorId VendorId { get; }
    public GuidPrefix SourceGuidPrefix { get; }

    public RtpsMessageReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        var (version, vendorId, prefix) = RtpsHeader.Read(buffer);
        Version = version;
        VendorId = vendorId;
        SourceGuidPrefix = prefix;
        _position = RtpsHeader.Size;
    }

    /// <summary>残りバイト数 (Submessage の続きが入っている可能性のある領域)。</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>
    /// 次の Submessage を読み出す。
    /// 戻り値: true=Submessage を取得、false=メッセージ末尾。
    /// body は呼び出し中のみ有効 (内部バッファのスライス)。
    /// </summary>
    public bool TryReadNext(out SubmessageHeader header, out ReadOnlySpan<byte> body)
    {
        header = default;
        body = default;

        if (_position >= _buffer.Length)
        {
            return false;
        }
        if (_position + SubmessageHeader.Size > _buffer.Length)
        {
            // 不完全な submessage header
            return false;
        }
        var hdr = SubmessageHeader.Read(_buffer.Slice(_position, SubmessageHeader.Size));
        _position += SubmessageHeader.Size;

        int bodyLength = ResolveBodyLength(hdr);

        if (_position + bodyLength > _buffer.Length)
        {
            // body が message を超える: 不正
            throw new InvalidDataException(
                $"Submessage body length {bodyLength} exceeds remaining buffer {_buffer.Length - _position}.");
        }

        header = hdr;
        body = _buffer.Slice(_position, bodyLength);
        _position += bodyLength;
        return true;
    }

    private int ResolveBodyLength(SubmessageHeader header)
    {
        if (header.Length != 0)
        {
            return header.Length;
        }

        return header.Kind switch
        {
            SubmessageKind.Pad => 0,
            SubmessageKind.InfoTimestamp
                when (header.Flags & SubmessageFlags.InfoTsInvalidate) != 0 => 0,
            _ => _buffer.Length - _position,
        };
    }
}
