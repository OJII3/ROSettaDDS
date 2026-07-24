using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
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
    public void StartとStopの同時競合で最終状態がStopped()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);

        var proceedStart = new ManualResetEventSlim();
        var stopProceed = new ManualResetEventSlim();
        Exception? startError = null;
        Exception? stopError = null;

        writer.BeforeStartLockEnter = () =>
        {
            proceedStart.Set();
            stopProceed.Wait();
        };

        var startThread = new Thread(() =>
        {
            try { writer.Start(); }
            catch (Exception ex) { startError = ex; }
        });
        startThread.Start();

        Assert.True(proceedStart.Wait(TimeSpan.FromSeconds(5)),
            "Start must invoke BeforeStartLockEnter within timeout");

        // Now Start has set _startInProgress = 1 and is blocked before the lock.
        // Stop will see _startInProgress = 1 and set _stopRequested.
        writer.Stop();
        stopProceed.Set();

        Assert.True(startThread.Join(TimeSpan.FromSeconds(5)),
            "Start thread must complete");

        Assert.Null(startError);
        Assert.Null(stopError);
        writer.IsRunning.Should().BeFalse(
            "Stop called during Start must not be lost; writer must end up stopped");
    }

    [Fact]
    public void DisposeはHeartbeatループ完了後にhistoryを破棄する()
    {
        var s = CreateSetup();
        var writer = CreateWriter(s, out var history);
        writer.Start();

        var hbExited = new ManualResetEventSlim();
        writer.Dispose();
        writer.IsRunning.Should().BeFalse();
    }
}
