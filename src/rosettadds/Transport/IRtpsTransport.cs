using ROSettaDDS.Common;

namespace ROSettaDDS.Transport;

/// <summary>
/// RTPS パケットの送受信を担うトランスポート抽象。
/// 実装例: <c>UdpTransport</c>、テスト用 <c>LoopbackTransport</c>。
/// </summary>
public interface IRtpsTransport : IDisposable
{
    /// <summary>このトランスポートがバインドされているローカル Locator。</summary>
    Locator LocalLocator { get; }

    /// <summary>パケットを指定の Locator に向けて送信する。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// パケット受信時に発火。
    /// 第一引数は受信ペイロード (寿命は呼び出し中のみ有効)、第二引数は送信元 Locator。
    /// </summary>
    event Action<ReadOnlyMemory<byte>, Locator>? Received;

    /// <summary>受信ループを開始する。</summary>
    void Start();

    /// <summary>受信ループを停止する (Dispose でも自動停止)。</summary>
    void Stop();
}
