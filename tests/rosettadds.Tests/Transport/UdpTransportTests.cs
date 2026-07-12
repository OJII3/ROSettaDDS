using System.Net;
using System.Net.Sockets;
using ROSettaDDS.Common;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Transport;

public class UdpTransportTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void Unicast_LocalLocator_は_実際にバインドされた_endpoint_を反映する()
    {
        using var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        transport.LocalLocator.Kind.Should().Be(LocatorKind.UdpV4);
        transport.LocalLocator.Port.Should().BeGreaterThan(0u, "ephemeral ポートが割り当てられているはず");
        transport.LocalLocator.ToIPAddress().Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public async Task Unicast_loopback_で_往復配信が成立する()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        var tcs = new TaskCompletionSource<(byte[] data, Locator source)>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Received += (data, src) => tcs.TrySetResult((data.ToArray(), src));
        receiver.Start();

        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        await sender.SendAsync(payload, receiver.LocalLocator);

        var result = await tcs.Task.WaitAsync(ReceiveTimeout);
        result.data.Should().Equal(payload);
        result.source.Kind.Should().Be(LocatorKind.UdpV4);
        result.source.Port.Should().Be(sender.LocalLocator.Port);
    }

    [Fact]
    public async Task Stop_で_受信ループが停止する()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        receiver.Start();
        await Task.Delay(50);
        receiver.Stop();
        // 二重 Stop も例外を投げない
        receiver.Stop();
    }

    [Fact]
    public async Task Stop_後に_Start_して_再受信できる()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        TaskCompletionSource<byte[]>? currentReceive = null;
        receiver.Received += (data, _) => currentReceive?.TrySetResult(data.ToArray());

        currentReceive = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Start();
        await sender.SendAsync(new byte[] { 0x10, 0x20 }, receiver.LocalLocator);
        var first = await currentReceive.Task.WaitAsync(ReceiveTimeout);
        first.Should().Equal(0x10, 0x20);

        receiver.Stop();

        currentReceive = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Start();
        await sender.SendAsync(new byte[] { 0x30, 0x40 }, receiver.LocalLocator);
        var second = await currentReceive.Task.WaitAsync(ReceiveTimeout);
        second.Should().Equal(0x30, 0x40);
    }

    [Fact]
    public async Task 受信handlerが遅くても_burst_受信を取りこぼしにくい()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        const int count = 200;
        int received = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Received += (_, _) =>
        {
            Thread.Sleep(2);
            if (Interlocked.Increment(ref received) == count)
            {
                allReceived.TrySetResult();
            }
        };
        receiver.Start();

        var payload = new byte[8192];
        for (int i = 0; i < count; i++)
        {
            await sender.SendAsync(payload, receiver.LocalLocator);
        }

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref received).Should().Be(count);
    }

    [Fact]
    public async Task Stop_は_enqueue済み受信packetを処理してから戻る()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        const int count = 3;
        int received = 0;
        receiver.Received += (_, _) =>
        {
            Thread.Sleep(600);
            Interlocked.Increment(ref received);
        };
        receiver.Start();

        for (int i = 0; i < count; i++)
        {
            await sender.SendAsync(new byte[] { (byte)i }, receiver.LocalLocator);
        }

        // 高負荷 CI で loopback 受信が 100ms では完了しないことがあるため、
        // Diagnostics.DatagramsEnqueued で enqueue 完了を待つ。
        var enqDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < enqDeadline && receiver.Diagnostics.DatagramsEnqueued < count)
        {
            await Task.Delay(20);
        }

        receiver.Stop();

        Volatile.Read(ref received).Should().Be(count);
    }

    [Fact]
    public async Task Received_handler_内で_Stop_してもデッドロックしない()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Received += (_, _) =>
        {
            receiver.Stop();
            stopped.TrySetResult();
        };
        receiver.Start();

        await sender.SendAsync(new byte[] { 1 }, receiver.LocalLocator);

        await stopped.Task.WaitAsync(ReceiveTimeout);
    }

    [Fact]
    public async Task Diagnostics_は受信queue境界の件数を返す()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Received += (_, _) => tcs.TrySetResult();
        receiver.Start();

        await sender.SendAsync(new byte[] { 1, 2, 3 }, receiver.LocalLocator);
        await tcs.Task.WaitAsync(ReceiveTimeout);

        var diagnostics = receiver.Diagnostics;
        diagnostics.DatagramsReceived.Should().Be(1);
        diagnostics.DatagramsEnqueued.Should().Be(1);
        diagnostics.DatagramsDispatched.Should().Be(1);
        diagnostics.DatagramsDropped.Should().Be(0);
        diagnostics.QueueCount.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_後の_SendAsync_は_ObjectDisposedException()
    {
        var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        var dest = transport.LocalLocator;
        transport.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.SendAsync(new byte[] { 1 }, dest));
    }

    [Fact]
    public async Task SendAsync_に_UDPv4_以外を渡すと_NotSupportedException()
    {
        using var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        var unsupported = new Locator(LocatorKind.Reserved, 7400u, new byte[16]);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await transport.SendAsync(new byte[] { 1 }, unsupported));
    }

    [Fact]
    public void Unicast_初期化失敗時は作成済みsocketを破棄する()
    {
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int blockedPort = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        Assert.Throws<SocketException>(() =>
            UdpTransport.CreateUnicast(IPAddress.Loopback, blockedPort));
    }

    [Fact]
    public void Multicast_初期化失敗時はbound_socketを破棄する()
    {
        int port = GetFreeUdpPort();

        Assert.ThrowsAny<Exception>(() =>
            UdpTransport.CreateMulticast(IPAddress.Loopback, port));

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>
    /// マルチキャスト loopback テスト。
    /// 環境によっては multicast loopback が無効化されている場合があり、その場合は SocketException でスキップ。
    /// </summary>
    [Fact]
    public async Task Multicast_自己受信_往復配信()
    {
        var group = IPAddress.Parse("239.255.42.123");
        int port = GetFreeUdpPort();

        UdpTransport transport;
        try
        {
            transport = UdpTransport.CreateMulticast(group, port, IPAddress.Loopback);
        }
        catch (SocketException ex)
        {
            // テスト環境がマルチキャストをサポートしていない場合
            Assert.Fail($"Multicast bind failed: {ex.Message}");
            return;
        }

        using (transport)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Received += (data, _) => tcs.TrySetResult(data.ToArray());
            transport.Start();

            // 自分自身が join したマルチキャストグループへ送る (multicast loopback)
            var dest = Locator.FromUdpV4(group, (uint)port);
            await transport.SendAsync(new byte[] { 0xAA, 0xBB, 0xCC }, dest);

            try
            {
                var received = await tcs.Task.WaitAsync(ReceiveTimeout);
                received.Should().Equal(0xAA, 0xBB, 0xCC);
            }
            catch (TimeoutException)
            {
                Assert.Fail("Multicast self-receive timed out — multicast loopback may be disabled in this environment.");
            }
        }
    }

    [Fact]
    public async Task Unicast_restart後も同じ_instanceと_handlerで受信を再開する()
    {
        using var receiver = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        using var sender = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        var originalLocator = receiver.LocalLocator;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Received += (data, _) =>
        {
            if (data.Span[0] == 0x01) first.TrySetResult();
            if (data.Span[0] == 0x02) second.TrySetResult();
        };
        receiver.Start();

        await sender.SendAsync(new byte[] { 0x01 }, originalLocator);
        await first.Task.WaitAsync(ReceiveTimeout);

        receiver.Restart();

        receiver.LocalLocator.Should().Be(originalLocator);
        await sender.SendAsync(new byte[] { 0x02 }, originalLocator);
        await second.Task.WaitAsync(ReceiveTimeout);
    }

    [Fact]
    public async Task Multicast_restart後も同じ_handlerで自己受信を再開する()
    {
        var group = IPAddress.Parse("239.255.42.124");
        int port = GetFreeUdpPort();
        using var transport = UdpTransport.CreateMulticast(group, port, IPAddress.Loopback);
        var destination = Locator.FromUdpV4(group, (uint)port);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Received += (data, _) =>
        {
            if (data.Span[0] == 0x11) first.TrySetResult();
            if (data.Span[0] == 0x22) second.TrySetResult();
        };
        transport.Start();

        await transport.SendAsync(new byte[] { 0x11 }, destination);
        await first.Task.WaitAsync(ReceiveTimeout);

        transport.Restart();

        await transport.SendAsync(new byte[] { 0x22 }, destination);
        await second.Task.WaitAsync(ReceiveTimeout);
    }

    [Fact]
    public void Dispose後の_restartは_ObjectDisposedException()
    {
        var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        transport.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.Restart());
    }

    [Fact]
    public void Start前の_restartは同じportを再bindする()
    {
        using var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
        var locator = transport.LocalLocator;

        transport.Restart();

        transport.LocalLocator.Should().Be(locator);
    }

    private static int GetFreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }
}
