using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class StatefulWriterLifecycleTests
{
    private sealed class Setup
    {
        public required LoopbackTransport Transport { get; init; }
        public required Locator Locator { get; init; }
        public required GuidPrefix Prefix { get; init; }
        public required EntityId EntityId { get; init; }
    }

    private static Setup CreateSetup()
    {
        var hub = new LoopbackHub();
        var loc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var tr = hub.Create(loc);
        return new Setup
        {
            Transport = tr,
            Locator = loc,
            Prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x01, 0x00, 0x01),
            EntityId = new EntityId(0x0000_0001u, EntityKind.UserDefinedWriterNoKey),
        };
    }

    private static StatefulWriter CreateWriter(Setup s, out WriterHistoryCache history)
    {
        var writerGuid = new Guid(s.Prefix, s.EntityId);
        history = new WriterHistoryCache(writerGuid);
        return new StatefulWriter(
            sendTransport: s.Transport,
            multicastDestination: s.Locator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.Prefix,
            writerEntityId: s.EntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            logger: NullLogger.Instance);
    }

    [Fact]
    public void Start後にIsRunningがtrue()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        writer.IsRunning.Should().BeFalse();

        writer.Start();
        writer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop後にIsRunningがfalse()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        writer.Start();
        writer.IsRunning.Should().BeTrue();

        writer.Stop();
        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void 二重Startは安全()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        writer.Start();
        writer.Start();
        writer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void 二重Stopは安全()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        writer.Start();
        writer.Stop();
        writer.Stop();
        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Stop_Start_Stopの遷移が正しい()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        writer.Start();
        writer.IsRunning.Should().BeTrue();

        writer.Stop();
        writer.IsRunning.Should().BeFalse();

        writer.Start();
        writer.IsRunning.Should().BeTrue();

        writer.Stop();
        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void StartとStartの並行競合で一方だけが起動する()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        var barrier = new Barrier(2);
        Exception? error1 = null;
        Exception? error2 = null;

        var t1 = new Thread(() =>
        {
            barrier.SignalAndWait();
            try { writer.Start(); }
            catch (Exception ex) { error1 = ex; }
        });
        var t2 = new Thread(() =>
        {
            barrier.SignalAndWait();
            try { writer.Start(); }
            catch (Exception ex) { error2 = ex; }
        });

        t1.Start();
        t2.Start();
        Assert.True(t1.Join(TimeSpan.FromSeconds(5)));
        Assert.True(t2.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error1);
        Assert.Null(error2);
        writer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void DisposeとStartの同時競合でDisposeが優先される()
    {
        var s = CreateSetup();
        var writer = CreateWriter(s, out _);

        var ready = new Barrier(2);
        Exception? startError = null;
        Exception? disposeError = null;

        var startThread = new Thread(() =>
        {
            ready.SignalAndWait();
            try { writer.Start(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { startError = ex; }
        });
        var disposeThread = new Thread(() =>
        {
            ready.SignalAndWait();
            try { writer.Dispose(); }
            catch (Exception ex) { disposeError = ex; }
        });

        startThread.Start();
        disposeThread.Start();
        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(startThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(disposeError);
        Assert.Null(startError);

        writer.IsRunning.Should().BeFalse();
        writer.History.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Heartbeatループ終了を実際にassert()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        int hbCount = 0;
        s.Transport.Received += (packetData, srcLoc) =>
        {
            if (!RtpsHeader.TryRead(packetData.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packetData.Span);
            while (reader.TryReadNext(out var subHeader, out _))
            {
                if (subHeader.Kind == SubmessageKind.Heartbeat)
                    Interlocked.Increment(ref hbCount);
            }
        };

        writer.Start();
        Thread.Sleep(_heartbeatPeriodMs * 3);
        int beforeStop = Volatile.Read(ref hbCount);
        beforeStop.Should().BeGreaterThan(1, "heartbeats should have been sent before stop");

        writer.Stop();
        Thread.Sleep(_heartbeatPeriodMs * 2);
        int afterStop = Volatile.Read(ref hbCount);
        afterStop.Should().Be(beforeStop, "no heartbeats after stop");

        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeatループ再起動後停止を実際にassert()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        int hbCount = 0;
        var hbReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Transport.Received += (packetData, srcLoc) =>
        {
            if (!RtpsHeader.TryRead(packetData.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packetData.Span);
            while (reader.TryReadNext(out var subHeader, out _))
            {
                if (subHeader.Kind == SubmessageKind.Heartbeat)
                {
                    int count = Interlocked.Increment(ref hbCount);
                    if (count >= 3) hbReceived.TrySetResult();
                }
            }
        };

        // Start → Stop → Start → Stop のサイクル
        writer.Start();
        await hbReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.Stop();
        int hbAfterFirstStop = Volatile.Read(ref hbCount);

        Thread.Sleep(_heartbeatPeriodMs * 2);
        Volatile.Read(ref hbCount).Should().Be(hbAfterFirstStop,
            "no heartbeats after first stop");

        writer.Start();
        hbReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread.Sleep(_heartbeatPeriodMs * 3);
        int hbAfterRestart = Volatile.Read(ref hbCount);
        hbAfterRestart.Should().BeGreaterThan(hbAfterFirstStop,
            "heartbeats resume after restart");

        writer.Stop();
        Thread.Sleep(_heartbeatPeriodMs * 2);
        Volatile.Read(ref hbCount).Should().Be(hbAfterRestart,
            "no heartbeats after second stop");

        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void DisposeはHeartbeatループ完了後にhistoryを破棄する()
    {
        var s = CreateSetup();
        var writer = CreateWriter(s, out var history);
        writer.Start();
        writer.Dispose();
        writer.IsRunning.Should().BeFalse();
        history.IsDisposed.Should().BeTrue();
    }

    private static readonly int _heartbeatPeriodMs = 50;
}
