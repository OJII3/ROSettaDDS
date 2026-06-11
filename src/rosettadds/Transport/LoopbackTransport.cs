using ROSettaDDS.Common;

namespace ROSettaDDS.Transport;

/// <summary>
/// プロセス内で複数の <see cref="LoopbackTransport"/> インスタンス間にパケットをルーティングするハブ。
/// 単体テストで実 socket を使わずに <see cref="IRtpsTransport"/> 契約を検証するために使う。
/// </summary>
public sealed class LoopbackHub
{
    private readonly object _lock = new();
    private readonly Dictionary<Locator, List<LoopbackTransport>> _listeners = new();

    /// <summary>
    /// 指定 Locator を LocalLocator とする <see cref="LoopbackTransport"/> を生成し、ハブに登録する。
    /// </summary>
    public LoopbackTransport Create(Locator localLocator)
    {
        var transport = new LoopbackTransport(this, localLocator);
        Subscribe(localLocator, transport);
        return transport;
    }

    /// <summary>追加 Locator (マルチキャスト想定) に listener を登録する。</summary>
    internal void Subscribe(Locator locator, LoopbackTransport transport)
    {
        lock (_lock)
        {
            if (!_listeners.TryGetValue(locator, out var list))
            {
                list = new List<LoopbackTransport>();
                _listeners[locator] = list;
            }
            if (!list.Contains(transport))
            {
                list.Add(transport);
            }
        }
    }

    internal void Unsubscribe(LoopbackTransport transport)
    {
        lock (_lock)
        {
            foreach (var list in _listeners.Values)
            {
                list.Remove(transport);
            }
        }
    }

    /// <summary>destination に登録されている全 listener にパケットを配信する。</summary>
    internal void Deliver(ReadOnlyMemory<byte> packet, Locator destination, Locator source)
    {
        LoopbackTransport[]? snapshot = null;
        lock (_lock)
        {
            if (_listeners.TryGetValue(destination, out var list) && list.Count > 0)
            {
                snapshot = list.ToArray();
            }
        }
        if (snapshot is null)
        {
            return;
        }
        foreach (var t in snapshot)
        {
            t.RaiseReceived(packet, source);
        }
    }
}

/// <summary>
/// プロセス内ループバックトランスポート。テストで使用。
/// SendAsync は同期的に <see cref="LoopbackHub"/> 経由で配信される。
/// </summary>
public sealed class LoopbackTransport : IRtpsTransport
{
    private readonly LoopbackHub _hub;
    private bool _disposed;

    public Locator LocalLocator { get; }
    public event Action<ReadOnlyMemory<byte>, Locator>? Received;

    internal LoopbackTransport(LoopbackHub hub, Locator localLocator)
    {
        _hub = hub;
        LocalLocator = localLocator;
    }

    /// <summary>追加の Locator (例: マルチキャストグループ) を listen する。</summary>
    public void JoinGroup(Locator groupLocator)
    {
        ThrowIfDisposed();
        _hub.Subscribe(groupLocator, this);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        // 配信側にコピーを渡す (呼び出し元バッファの再利用に対する安全策)
        var copy = packet.ToArray();
        _hub.Deliver(copy, destination, LocalLocator);
        return default;
    }

    /// <summary>Loopback では受信ループ不要のため no-op。</summary>
    public void Start() { ThrowIfDisposed(); }

    /// <summary>Loopback では no-op。</summary>
    public void Stop() { }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _hub.Unsubscribe(this);
    }

    internal void RaiseReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        if (_disposed)
        {
            return;
        }
        Received?.Invoke(packet, source);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LoopbackTransport));
        }
    }
}
