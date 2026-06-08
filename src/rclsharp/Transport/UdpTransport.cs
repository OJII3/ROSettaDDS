using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rclsharp.Common;
using Rclsharp.Common.Logging;

namespace Rclsharp.Transport;

/// <summary>
/// UDP ベースの <see cref="IRtpsTransport"/> 実装。
/// ユニキャスト受信用とマルチキャスト受信用をファクトリメソッドで切り替える。
/// 送信は同一ソケットからユニキャスト/マルチキャスト両方に可能。
/// </summary>
/// <remarks>
/// 受信ループは <see cref="Start"/> で起動する。<see cref="Stop"/> または <see cref="Dispose"/> で停止。
/// 受信ハンドラ (<see cref="Received"/>) に渡す <see cref="ReadOnlyMemory{T}"/> は呼び出し中のみ有効
/// (<see cref="ArrayPool{T}"/> から借りたバッファを再利用する)。保持したい場合は呼び出し側で複製すること。
/// </remarks>
public sealed class UdpTransport : IRtpsTransport
{
    private const int ReceivePollTimeoutMilliseconds = 100;

    private readonly Socket _socket;
    private readonly Locator _localLocator;
    private readonly bool _isMulticast;
    private readonly IPAddress? _multicastGroup;
    private readonly ILogger _logger;
    private readonly int _receiveBufferSize;

    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    public Locator LocalLocator => _localLocator;

    public event Action<ReadOnlyMemory<byte>, Locator>? Received;

    private UdpTransport(
        Socket socket,
        Locator localLocator,
        bool isMulticast,
        IPAddress? multicastGroup,
        ILogger logger,
        int receiveBufferSize)
    {
        _socket = socket;
        _socket.ReceiveTimeout = ReceivePollTimeoutMilliseconds;
        _localLocator = localLocator;
        _isMulticast = isMulticast;
        _multicastGroup = multicastGroup;
        _logger = logger;
        _receiveBufferSize = receiveBufferSize;
    }

    /// <summary>
    /// ユニキャスト用ソケットを生成する。
    /// </summary>
    /// <param name="bindAddress">バインドする IPv4 アドレス (例: <see cref="IPAddress.Any"/>, <see cref="IPAddress.Loopback"/>)。</param>
    /// <param name="port">バインドするポート。0 を指定すると ephemeral ポートが割り当てられる。</param>
    /// <param name="logger">受信エラー等のログ出力。null なら破棄。</param>
    /// <param name="receiveBufferSize">受信バッファサイズ (各受信時に確保)。既定は 65535 (UDP 最大)。</param>
    public static UdpTransport CreateUnicast(
        IPAddress bindAddress,
        int port,
        ILogger? logger = null,
        int receiveBufferSize = 65535)
    {
        if (bindAddress is null) throw new ArgumentNullException(nameof(bindAddress));
        if (bindAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 bindAddress supported.", nameof(bindAddress));
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(bindAddress, port));
            var ep = (IPEndPoint)socket.LocalEndPoint!;
            var locator = Locator.FromUdpV4(ep.Address, (uint)ep.Port);
            return new UdpTransport(
                socket, locator,
                isMulticast: false, multicastGroup: null,
                logger ?? NullLogger.Instance, receiveBufferSize);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// マルチキャスト受信用ソケットを生成する。指定グループに join する。
    /// </summary>
    /// <param name="multicastGroup">join する IPv4 マルチキャストアドレス (例: 239.255.0.1)。</param>
    /// <param name="port">バインドするポート (= マルチキャストグループのポート)。</param>
    /// <param name="joinInterface">join するローカル NIC の IPv4 アドレス。null なら全 NIC (= IPAddress.Any)。</param>
    /// <param name="logger">受信エラー等のログ出力。</param>
    /// <param name="receiveBufferSize">受信バッファサイズ。</param>
    /// <param name="multicastTimeToLive">マルチキャスト送信時の TTL。既定 1 (リンクローカル)。</param>
    public static UdpTransport CreateMulticast(
        IPAddress multicastGroup,
        int port,
        IPAddress? joinInterface = null,
        ILogger? logger = null,
        int receiveBufferSize = 65535,
        int multicastTimeToLive = 1)
    {
        if (multicastGroup is null) throw new ArgumentNullException(nameof(multicastGroup));
        if (multicastGroup.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 multicast supported.", nameof(multicastGroup));
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // 全 NIC からマルチキャスト受信を取りこぼさないため ANY:port にバインドする
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            // Join multicast group
            var iface = joinInterface ?? IPAddress.Any;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(multicastGroup, iface));

            // 送信側マルチキャスト設定
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, multicastTimeToLive);
            if (joinInterface is not null && !joinInterface.Equals(IPAddress.Any))
            {
                // 送信時のマルチキャスト送信元 NIC を明示
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, joinInterface.GetAddressBytes());
            }

            var actualPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            var locator = Locator.FromUdpV4(multicastGroup, (uint)actualPort);
            return new UdpTransport(
                socket, locator,
                isMulticast: true, multicastGroup: multicastGroup,
                logger ?? NullLogger.Instance, receiveBufferSize);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> packet,
        Locator destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = LocatorToEndPoint(destination);
        var segment = MemoryMarshal.TryGetArray(packet, out var s)
            ? s
            : new ArraySegment<byte>(packet.ToArray());
        await _socket.SendToAsync(segment, SocketFlags.None, endpoint).ConfigureAwait(false);
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_receiveTask is not null)
        {
            return;
        }
        _receiveCts = new CancellationTokenSource();
        var token = _receiveCts.Token;
        _receiveTask = Task.Run(() => ReceiveLoop(token), token);
    }

    public void Stop()
    {
        if (_receiveCts is null)
        {
            return;
        }
        _receiveCts.Cancel();
        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // 想定内
        }
        catch (Exception ex)
        {
            _logger.Warn("UdpTransport receive task did not exit cleanly", ex);
        }
        _receiveCts.Dispose();
        _receiveCts = null;
        _receiveTask = null;
    }

    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] buffer = pool.Rent(_receiveBufferSize);
            try
            {
                EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                int receivedBytes = _socket.ReceiveFrom(
                    buffer,
                    0,
                    _receiveBufferSize,
                    SocketFlags.None,
                    ref endpoint);

                if (endpoint is not IPEndPoint src)
                {
                    continue;
                }

                var sourceLocator = src.Address.AddressFamily == AddressFamily.InterNetwork
                    ? Locator.FromUdpV4(src.Address, (uint)src.Port)
                    : Locator.FromUdpV6(src.Address, (uint)src.Port);

                Received?.Invoke(buffer.AsMemory(0, receivedBytes), sourceLocator);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("UdpTransport receive error", ex);
            }
            finally
            {
                pool.Return(buffer);
            }
        }
    }

    private static IPEndPoint LocatorToEndPoint(Locator destination)
    {
        return destination.Kind switch
        {
            LocatorKind.UdpV4 => new IPEndPoint(destination.ToIPAddress(), (int)destination.Port),
            LocatorKind.UdpV6 => new IPEndPoint(destination.ToIPAddress(), (int)destination.Port),
            _ => throw new NotSupportedException(
                $"UdpTransport supports only UDPv4/UDPv6 destinations. Got {destination.Kind}."),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        if (_isMulticast && _multicastGroup is not null)
        {
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                    new MulticastOption(_multicastGroup));
            }
            catch
            {
                // best-effort
            }
        }
        _socket.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
