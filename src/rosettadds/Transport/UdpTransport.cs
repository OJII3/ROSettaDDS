using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Transport;

/// <summary>
/// UDP ベースの <see cref="IRtpsTransport"/> 実装。
/// ユニキャスト受信用とマルチキャスト受信用をファクトリメソッドで切り替える。
/// 送信は同一ソケットからユニキャスト/マルチキャスト両方に可能。
/// </summary>
/// <remarks>
/// 受信ループは <see cref="Start"/> で起動する。<see cref="Stop"/> または <see cref="Dispose"/> で停止。
/// 受信ハンドラ (<see cref="Received"/>) に渡す <see cref="ReadOnlyMemory{T}"/> は呼び出し中のみ有効。
/// 保持したい場合は呼び出し側で複製すること。
/// </remarks>
public sealed class UdpTransport : IRtpsTransport
{
    private const int ReceivePollTimeoutMilliseconds = 100;
    private const int RequestedSocketReceiveBufferSize = 4 * 1024 * 1024;
    private const int MaxQueuedReceivedPackets = 8192;
    private static int s_activeReceiveLoopCount;

    private Socket _socket;
    private Locator _localLocator;
    private readonly bool _isMulticast;
    private readonly IPAddress? _multicastGroup;
    private readonly IPAddress _bindAddress;
    private readonly int _boundPort;
    private readonly IPAddress? _joinInterface;
    private readonly int _multicastTimeToLive;
    private readonly ILogger _logger;
    private readonly int _receiveBufferSize;
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private BlockingCollection<ReceivedPacket>? _dispatchQueue;
    private Task? _dispatchTask;
    private long _datagramsReceived;
    private long _datagramsEnqueued;
    private long _datagramsDropped;
    private long _datagramsDispatched;
    private bool _disposed;

    public Locator LocalLocator => _localLocator;

    internal static int ActiveReceiveLoopCount => Volatile.Read(ref s_activeReceiveLoopCount);

    public UdpTransportDiagnostics Diagnostics => new(
        Volatile.Read(ref _datagramsReceived),
        Volatile.Read(ref _datagramsEnqueued),
        Volatile.Read(ref _datagramsDropped),
        Volatile.Read(ref _datagramsDispatched),
        _dispatchQueue?.Count ?? 0);

    public event Action<ReadOnlyMemory<byte>, Locator>? Received;

    private UdpTransport(
        Socket socket,
        Locator localLocator,
        bool isMulticast,
        IPAddress? multicastGroup,
        IPAddress bindAddress,
        IPAddress? joinInterface,
        int multicastTimeToLive,
        ILogger logger,
        int receiveBufferSize)
    {
        _socket = socket;
        _socket.ReceiveTimeout = ReceivePollTimeoutMilliseconds;
        _localLocator = localLocator;
        _isMulticast = isMulticast;
        _multicastGroup = multicastGroup;
        _bindAddress = bindAddress;
        _boundPort = checked((int)localLocator.Port);
        _joinInterface = joinInterface;
        _multicastTimeToLive = multicastTimeToLive;
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

        var (socket, locator) = CreateUnicastSocket(bindAddress, port);
        return new UdpTransport(
            socket,
            locator,
            isMulticast: false,
            multicastGroup: null,
            bindAddress,
            joinInterface: null,
            multicastTimeToLive: 1,
            logger ?? NullLogger.Instance,
            receiveBufferSize);
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

        var (socket, locator) = CreateMulticastSocket(
            multicastGroup,
            port,
            joinInterface,
            multicastTimeToLive);
        return new UdpTransport(
            socket,
            locator,
            isMulticast: true,
            multicastGroup,
            bindAddress: IPAddress.Any,
            joinInterface,
            multicastTimeToLive,
            logger ?? NullLogger.Instance,
            receiveBufferSize);
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
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _socket.SendToAsync(segment, SocketFlags.None, endpoint).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            StartCore();
        }
    }

    private void StartCore()
    {
        if (_receiveTask is not null)
        {
            return;
        }
        _receiveCts = new CancellationTokenSource();
        _dispatchQueue = new BlockingCollection<ReceivedPacket>(MaxQueuedReceivedPackets);
        var token = _receiveCts.Token;
        _dispatchTask = Task.Run(DispatchLoop, token);
        _receiveTask = Task.Run(() => ReceiveLoop(token), token);
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_receiveCts is null)
        {
            return;
        }
        bool calledFromDispatchTask = Task.CurrentId == _dispatchTask?.Id;
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
        _dispatchQueue?.CompleteAdding();
        if (!calledFromDispatchTask)
        {
            try
            {
                _dispatchTask?.Wait();
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
            {
                // 想定内
            }
            catch (Exception ex)
            {
                _logger.Warn("UdpTransport dispatch task did not exit cleanly", ex);
            }
        }
        _receiveCts.Dispose();
        _receiveCts = null;
        _receiveTask = null;
    }

    internal void Restart()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            bool wasStarted = _receiveTask is not null;
            StopCore();

            _sendGate.Wait();
            try
            {
                DropMembershipAndDisposeSocket(_socket);
                var replacement = CreateConfiguredSocket();
                _socket = replacement.Socket;
                _localLocator = replacement.Locator;
            }
            finally
            {
                _sendGate.Release();
            }

            if (wasStarted)
            {
                StartCore();
            }
        }
    }

    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;
        Interlocked.Increment(ref s_activeReceiveLoopCount);
        try
        {
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
                    Interlocked.Increment(ref _datagramsReceived);

                    var sourceLocator = src.Address.AddressFamily == AddressFamily.InterNetwork
                        ? Locator.FromUdpV4(src.Address, (uint)src.Port)
                        : Locator.FromUdpV6(src.Address, (uint)src.Port);

                    var packet = new byte[receivedBytes];
                    Buffer.BlockCopy(buffer, 0, packet, 0, receivedBytes);
                    EnqueueReceivedPacket(new ReceivedPacket(packet, sourceLocator));
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
        finally
        {
            Interlocked.Decrement(ref s_activeReceiveLoopCount);
        }
    }

    private void EnqueueReceivedPacket(ReceivedPacket packet)
    {
        var queue = _dispatchQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            Interlocked.Increment(ref _datagramsDropped);
            return;
        }

        try
        {
            if (queue.TryAdd(packet))
            {
                Interlocked.Increment(ref _datagramsEnqueued);
            }
            else
            {
                Interlocked.Increment(ref _datagramsDropped);
            }
        }
        catch (InvalidOperationException)
        {
            // Stop と競合して CompleteAdding 済みの場合は破棄する。
            Interlocked.Increment(ref _datagramsDropped);
        }
    }

    private void DispatchLoop()
    {
        var queue = _dispatchQueue;
        if (queue is null)
        {
            return;
        }

        try
        {
            foreach (var packet in queue.GetConsumingEnumerable())
            {
                try
                {
                    Received?.Invoke(packet.Data, packet.Source);
                }
                catch (Exception ex)
                {
                    _logger.Error("UdpTransport dispatch error", ex);
                }
                finally
                {
                    Interlocked.Increment(ref _datagramsDispatched);
                }
            }
        }
        finally
        {
            queue.Dispose();
            if (ReferenceEquals(_dispatchQueue, queue))
            {
                _dispatchQueue = null;
                _dispatchTask = null;
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

    private static void ConfigureReceiveBuffer(Socket socket)
    {
        socket.ReceiveTimeout = ReceivePollTimeoutMilliseconds;
        socket.ReceiveBufferSize = Math.Max(socket.ReceiveBufferSize, RequestedSocketReceiveBufferSize);
    }

    private static (Socket Socket, Locator Locator) CreateUnicastSocket(
        IPAddress bindAddress,
        int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            ConfigureReceiveBuffer(socket);
            // SO_REUSEADDR は設定しない。Linux で同一ポートへの二重 bind を許すと
            // ParticipantId auto-probe の前提が崩れるため。
            socket.Bind(new IPEndPoint(bindAddress, port));
            var endpoint = (IPEndPoint)socket.LocalEndPoint!;
            return (socket, Locator.FromUdpV4(endpoint.Address, (uint)endpoint.Port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static (Socket Socket, Locator Locator) CreateMulticastSocket(
        IPAddress multicastGroup,
        int port,
        IPAddress? joinInterface,
        int multicastTimeToLive)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            ConfigureReceiveBuffer(socket);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            var iface = joinInterface ?? IPAddress.Any;
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(multicastGroup, iface));
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive,
                multicastTimeToLive);
            if (joinInterface is not null && !joinInterface.Equals(IPAddress.Any))
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    joinInterface.GetAddressBytes());
            }

            var actualPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            return (socket, Locator.FromUdpV4(multicastGroup, (uint)actualPort));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private (Socket Socket, Locator Locator) CreateConfiguredSocket()
        => _isMulticast
            ? CreateMulticastSocket(
                _multicastGroup!,
                _boundPort,
                _joinInterface,
                _multicastTimeToLive)
            : CreateUnicastSocket(_bindAddress, _boundPort);

    private void DropMembershipAndDisposeSocket(Socket socket)
    {
        if (_isMulticast && _multicastGroup is not null)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.DropMembership,
                    new MulticastOption(_multicastGroup, _joinInterface ?? IPAddress.Any));
            }
            catch
            {
                // socket破棄前のbest-effort cleanup。
            }
        }
        socket.Dispose();
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopCore();
            _sendGate.Wait();
            try
            {
                DropMembershipAndDisposeSocket(_socket);
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    private readonly struct ReceivedPacket
    {
        public ReceivedPacket(byte[] data, Locator source)
        {
            Data = data;
            Source = source;
        }

        public byte[] Data { get; }
        public Locator Source { get; }
    }
}
