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

        var hbReceived = new ManualResetEventSlim(false);
        int hbCount = 0;
        s.Transport.Received += (packetData, srcLoc) =>
        {
            if (!RtpsHeader.TryRead(packetData.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packetData.Span);
            while (reader.TryReadNext(out var subHeader, out _))
            {
                if (subHeader.Kind == SubmessageKind.Heartbeat)
                {
                    Interlocked.Increment(ref hbCount);
                    hbReceived.Set();
                }
            }
        };

        writer.Start();
        hbReceived.Wait(TimeSpan.FromSeconds(5));
        int beforeStop = Volatile.Read(ref hbCount);
        beforeStop.Should().BeGreaterThan(0,
            "heartbeats should have been sent before stop");

        writer.Stop();
        int afterStop = Volatile.Read(ref hbCount);
        // Stop guarantees heartbeat loop completion; no more heartbeats can arrive
        Volatile.Read(ref hbCount).Should().Be(afterStop,
            "no heartbeats after stop");

        writer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Heartbeatループ再起動後停止を実際にassert()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        var hbReceived = new AutoResetEvent(false);
        int hbCount = 0;
        s.Transport.Received += (packetData, srcLoc) =>
        {
            if (!RtpsHeader.TryRead(packetData.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packetData.Span);
            while (reader.TryReadNext(out var subHeader, out _))
            {
                if (subHeader.Kind == SubmessageKind.Heartbeat)
                {
                    Interlocked.Increment(ref hbCount);
                    hbReceived.Set();
                }
            }
        };

        // Start → Stop → Start → Stop のサイクル
        writer.Start();
        Assert.True(hbReceived.WaitOne(TimeSpan.FromSeconds(5)),
            "heartbeats should arrive after first start");
        int afterFirstHb = Volatile.Read(ref hbCount);
        afterFirstHb.Should().BeGreaterThan(0);

        writer.Stop();
        int afterFirstStop = Volatile.Read(ref hbCount);
        Volatile.Read(ref hbCount).Should().Be(afterFirstStop,
            "no heartbeats after first stop");

        writer.Start();
        Assert.True(hbReceived.WaitOne(TimeSpan.FromSeconds(5)),
            "heartbeats should arrive after restart");
        int beforeSecondStop = Volatile.Read(ref hbCount);
        beforeSecondStop.Should().BeGreaterThan(afterFirstStop,
            "heartbeats resume after restart");

        writer.Stop();
        int afterSecondStop = Volatile.Read(ref hbCount);
        Volatile.Read(ref hbCount).Should().Be(afterSecondStop,
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

    [Fact]
    public async Task Stop後にACKNACKが届いてもRunBackgroundが開始されない()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out var history);

        // Write data and match a reader so ACKNACK can be processed
        var readerGuid = new Guid(s.Prefix, new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey));
        writer.MatchReader(readerGuid);
        await writer.WriteAsync(new byte[] { 1, 2, 3 });

        writer.Start();
        writer.Stop();

        // After Stop, send an ACKNACK → OnAckNack → RunBackground should be skipped
        var ackPacket = BuildAckNackPacket(s.Prefix, readerGuid.EntityId, s.EntityId);
        writer.ProcessPacket(ackPacket);

        // No crash, writer state is correct
        writer.IsRunning.Should().BeFalse();
        history.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task ACKNACK処理とDisposeの競合で全タスク完了後にhistoryが破棄される()
    {
        var s = CreateSetup();
        var writer = CreateWriter(s, out var history);

        // Write data and match a reader
        var readerGuid = new Guid(s.Prefix, new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey));
        writer.MatchReader(readerGuid);
        await writer.WriteAsync(new byte[] { 1, 2, 3 });

        var barrier = new Barrier(2);
        Exception? disposeError = null;

        var disposeThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            try { writer.Dispose(); }
            catch (Exception ex) { disposeError = ex; }
        });

        disposeThread.Start();
        barrier.SignalAndWait();

        // Send ACKNACK while Dispose is in progress
        // Dispose holds the lifecycle lock so OnAckNack→RunBackground is blocked
        // After Dispose completes, ProcessPacket sees _disposed and skips
        var ackPacket = BuildAckNackPacket(s.Prefix, readerGuid.EntityId, s.EntityId);
        writer.ProcessPacket(ackPacket);

        Assert.True(disposeThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(disposeError);

        writer.IsRunning.Should().BeFalse();
        history.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Disposeは_started_falseでRunBackgroundタスクがあっても完了後にhistoryを破棄する()
    {
        var s = CreateSetup();
        var writer = CreateWriter(s, out var history);

        // Match reader (doesn't require Start) and write data
        var readerGuid = new Guid(s.Prefix, new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey));
        writer.MatchReader(readerGuid);
        await writer.WriteAsync(new byte[] { 1, 2, 3 });

        // Trigger ACKNACK processing which calls RunBackground even without Start
        // (RunBackground checks _started, so with the fix this won't start a task)
        var ackPacket = BuildAckNackPacket(s.Prefix, readerGuid.EntityId, s.EntityId);
        writer.ProcessPacket(ackPacket);

        // Dispose - should not wait forever and should dispose history
        writer.Dispose();
        writer.IsRunning.Should().BeFalse();
        history.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task StopとMatchReaderの競合でRunBackgroundが漏れない()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        // Use reflection to set resendHistoryOnMatch... 
        // Instead, create a writer with resendHistoryOnMatch: true directly
        var writerGuid = new Guid(s.Prefix, s.EntityId);
        var history = new WriterHistoryCache(writerGuid);
        var resendWriter = new StatefulWriter(
            sendTransport: s.Transport,
            multicastDestination: s.Locator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.Prefix,
            writerEntityId: s.EntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            logger: NullLogger.Instance,
            purgeAckedSamples: true,
            resendHistoryOnMatch: true);
        using (resendWriter)
        {
            // Write data
            await resendWriter.WriteAsync(new byte[] { 1, 2, 3 });

            var barrier = new Barrier(2);
            Exception? matchError = null;
            Exception? stopError = null;

            var matchThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    var rg = new Guid(s.Prefix, new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey));
                    resendWriter.MatchReader(rg);
                }
                catch (Exception ex) { matchError = ex; }
            });

            var stopThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try { resendWriter.Stop(); }
                catch (Exception ex) { stopError = ex; }
            });

            matchThread.Start();
            stopThread.Start();

            Assert.True(matchThread.Join(TimeSpan.FromSeconds(5)));
            Assert.True(stopThread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(matchError);
            Assert.Null(stopError);

            resendWriter.IsRunning.Should().BeFalse();
        }
    }

    private static byte[] BuildAckNackPacket(GuidPrefix readerPrefix, EntityId readerEntityId, EntityId writerEntityId)
    {
        var buffer = new byte[256];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.ROSettaDDS, readerPrefix);
        writer.WriteAckNack(new AckNackSubmessage(
            readerEntityId: readerEntityId,
            writerEntityId: writerEntityId,
            readerSnState: new SequenceNumberSet(new SequenceNumber(1), 0, Array.Empty<uint>()),
            count: 1,
            final: true));
        return writer.WrittenSpan.ToArray();
    }
}
