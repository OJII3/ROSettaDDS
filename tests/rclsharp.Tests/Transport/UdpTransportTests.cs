using System.Net;
using System.Net.Sockets;
using Rclsharp.Common;
using Rclsharp.Transport;

namespace Rclsharp.Tests.Transport;

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

    private static int GetFreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }
}
