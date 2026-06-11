using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Transport;

public class LoopbackTransportTests
{
    private static Locator Loc(string ip, int port)
        => Locator.FromUdpV4(IPAddress.Parse(ip), (uint)port);

    [Fact]
    public async Task SendAsync_で_対象側の_Received_イベントが発火する()
    {
        var hub = new LoopbackHub();
        var locA = Loc("10.0.0.1", 7410);
        var locB = Loc("10.0.0.2", 7412);
        using var a = hub.Create(locA);
        using var b = hub.Create(locB);

        Locator? receivedFrom = null;
        byte[]? receivedPayload = null;
        b.Received += (data, src) =>
        {
            receivedFrom = src;
            receivedPayload = data.ToArray();
        };

        await a.SendAsync(new byte[] { 1, 2, 3 }, locB);

        receivedFrom.Should().Be(locA);
        receivedPayload.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task 未登録の_destination_に送ると_誰も受信しない()
    {
        var hub = new LoopbackHub();
        var locA = Loc("10.0.0.1", 7410);
        var locUnknown = Loc("10.0.0.99", 9999);
        using var a = hub.Create(locA);

        // 例外が出ず、何も起きないこと
        await a.SendAsync(new byte[] { 1, 2, 3 }, locUnknown);
    }

    [Fact]
    public async Task JoinGroup_で_マルチキャスト疑似配信が複数に届く()
    {
        var hub = new LoopbackHub();
        var locA = Loc("10.0.0.1", 7410);
        var locB = Loc("10.0.0.2", 7412);
        var locC = Loc("10.0.0.3", 7414);
        var group = Loc("239.255.0.1", 7400);
        using var a = hub.Create(locA);
        using var b = hub.Create(locB);
        using var c = hub.Create(locC);
        b.JoinGroup(group);
        c.JoinGroup(group);

        int bCount = 0, cCount = 0;
        b.Received += (_, _) => Interlocked.Increment(ref bCount);
        c.Received += (_, _) => Interlocked.Increment(ref cCount);

        await a.SendAsync(new byte[] { 0xAA }, group);

        bCount.Should().Be(1);
        cCount.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_後の送信は_ObjectDisposedException()
    {
        var hub = new LoopbackHub();
        var loc = Loc("10.0.0.1", 7410);
        var t = hub.Create(loc);
        t.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await t.SendAsync(new byte[] { 1 }, loc));
    }

    [Fact]
    public async Task Dispose_後は_Received_が呼ばれない()
    {
        var hub = new LoopbackHub();
        var locA = Loc("10.0.0.1", 7410);
        var locB = Loc("10.0.0.2", 7412);
        using var a = hub.Create(locA);
        var b = hub.Create(locB);

        bool received = false;
        b.Received += (_, _) => received = true;
        b.Dispose();

        // hub から登録解除されているので Deliver しても発火しない
        await a.SendAsync(new byte[] { 1 }, locB);
        received.Should().BeFalse();
    }
}
