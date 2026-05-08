using System.Net;
using Rclsharp.Common;
using Rclsharp.Rtps.HistoryCache;
using Rclsharp.Rtps.Reader;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Rtps.Writer;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Tests.Rtps;

public class StatefulHandshakeTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    private sealed class Setup
    {
        public required LoopbackHub Hub { get; init; }
        public required LoopbackTransport WriterTransport { get; init; }
        public required LoopbackTransport ReaderTransport { get; init; }
        public required Locator WriterLocator { get; init; }
        public required Locator ReaderLocator { get; init; }
        public required GuidPrefix WriterPrefix { get; init; }
        public required GuidPrefix ReaderPrefix { get; init; }
        public required EntityId WriterEntityId { get; init; }
        public required EntityId ReaderEntityId { get; init; }
    }

    private static Setup CreateSetup()
    {
        var hub = new LoopbackHub();
        var writerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u);
        var readerLoc = Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u);
        var writerTr = hub.Create(writerLoc);
        var readerTr = hub.Create(readerLoc);
        return new Setup
        {
            Hub = hub,
            WriterTransport = writerTr,
            ReaderTransport = readerTr,
            WriterLocator = writerLoc,
            ReaderLocator = readerLoc,
            WriterPrefix = GuidPrefix.Create(VendorId.Rclsharp, 0x11, 0x22, 0x01),
            ReaderPrefix = GuidPrefix.Create(VendorId.Rclsharp, 0x11, 0x22, 0x02),
            WriterEntityId = new EntityId(0x0000_0001u, EntityKind.UserDefinedWriterNoKey),
            ReaderEntityId = new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey),
        };
    }

    private static (StatefulWriter writer, StatefulReader reader) BuildPair(Setup s, TimeSpan heartbeatPeriod)
    {
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);

        var history = new WriterHistoryCache(writerGuid);
        var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator, // テストでは reader を直接 destination に
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: heartbeatPeriod,
            history: history);
        var reader = new StatefulReader(
            replyTransport: s.ReaderTransport,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.ReaderPrefix,
            readerEntityId: s.ReaderEntityId,
            ackNackFallbackDestination: s.WriterLocator);

        // wire up packet routing
        s.WriterTransport.Received += writer.OnPacketReceived;
        s.ReaderTransport.Received += reader.OnPacketReceived;

        // matching
        writer.MatchReader(readerGuid, s.ReaderLocator);
        reader.MatchWriter(writerGuid, s.WriterLocator);

        return (writer, reader);
    }

    [Fact]
    public async Task DATA_と_HEARTBEAT_と_ACKNACK_の_handshake()
    {
        var s = CreateSetup();
        var (writer, reader) = BuildPair(s, heartbeatPeriod: TimeSpan.FromMilliseconds(50));
        using (writer)
        using (reader)
        {
            var receivedTcs = new TaskCompletionSource<CacheChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.PayloadReceived += change => receivedTcs.TrySetResult(change);

            writer.Start();

            await writer.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });

            var change = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
            change.SequenceNumber.Value.Should().Be(1L);
            change.SerializedPayload.ToArray().Should().Equal(0x01, 0x02, 0x03);

            // HB が flow して ACKNACK で acked になるまで少し待つ
            await Task.Delay(200);

            var proxy = writer.GetReaderProxy(new Guid(s.ReaderPrefix, s.ReaderEntityId));
            proxy.Should().NotBeNull();
            proxy!.HighestAcked.Value.Should().BeGreaterOrEqualTo(1L);
        }
    }

    [Fact]
    public async Task 連続_5件_を_writer_から_reader_へ_順序通り受信()
    {
        var s = CreateSetup();
        var (writer, reader) = BuildPair(s, heartbeatPeriod: TimeSpan.FromMilliseconds(50));
        using (writer)
        using (reader)
        {
            var received = new List<long>();
            var lockObj = new object();
            var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.PayloadReceived += change =>
            {
                lock (lockObj)
                {
                    received.Add(change.SequenceNumber.Value);
                    if (received.Count == 5)
                    {
                        allReceived.TrySetResult(true);
                    }
                }
            };

            writer.Start();

            for (int i = 0; i < 5; i++)
            {
                await writer.WriteAsync(new byte[] { (byte)i });
            }

            await allReceived.Task.WaitAsync(ReceiveTimeout);
            lock (lockObj)
            {
                received.Should().Equal(1L, 2L, 3L, 4L, 5L);
            }
        }
    }

    [Fact]
    public async Task ACKNACK_で_writer_の_HighestAcked_が_進む()
    {
        var s = CreateSetup();
        var (writer, reader) = BuildPair(s, heartbeatPeriod: TimeSpan.FromMilliseconds(30));
        using (writer)
        using (reader)
        {
            writer.Start();
            for (int i = 0; i < 3; i++)
            {
                await writer.WriteAsync(new byte[] { (byte)i });
            }

            // HB→ACKNACK が 1 サイクル回るのを待つ
            await Task.Delay(200);

            var proxy = writer.GetReaderProxy(new Guid(s.ReaderPrefix, s.ReaderEntityId))!;
            proxy.HighestAcked.Value.Should().Be(3L);
        }
    }

    [Fact]
    public void WriterProxy_の_BuildAckNackBitmap_で_欠損が_set_される()
    {
        var writerGuid = new Guid(GuidPrefix.Unknown, EntityId.Unknown);
        var proxy = new WriterProxy(writerGuid);
        proxy.UpdateHeartbeatRange(new SequenceNumber(1L), new SequenceNumber(5L));

        // 1, 3 を受信、2, 4, 5 が欠損
        proxy.MarkReceived(new SequenceNumber(1L));
        proxy.MarkReceived(new SequenceNumber(3L));

        var snSet = proxy.BuildAckNackBitmap();
        // base = 連続受信 1 の次 = 2
        snSet.BitmapBase.Value.Should().Be(2L);
        snSet.NumBits.Should().Be(4); // [2, 3, 4, 5]

        snSet.IsSet(0).Should().BeTrue();  // 2 missing
        snSet.IsSet(1).Should().BeFalse(); // 3 received
        snSet.IsSet(2).Should().BeTrue();  // 4 missing
        snSet.IsSet(3).Should().BeTrue();  // 5 missing

        proxy.MissingSequenceNumbers().Select(s => s.Value).Should().Equal(2L, 4L, 5L);
    }

    [Fact]
    public void WriterProxy_全件受信時は_numBits_0_の_positive_ack()
    {
        var proxy = new WriterProxy(new Guid(GuidPrefix.Unknown, EntityId.Unknown));
        proxy.UpdateHeartbeatRange(new SequenceNumber(1L), new SequenceNumber(3L));
        proxy.MarkReceived(new SequenceNumber(1L));
        proxy.MarkReceived(new SequenceNumber(2L));
        proxy.MarkReceived(new SequenceNumber(3L));

        var snSet = proxy.BuildAckNackBitmap();
        snSet.BitmapBase.Value.Should().Be(4L);
        snSet.NumBits.Should().Be(0);
    }

    [Fact]
    public void ReaderProxy_の_ProcessAckNack_で_HighestAcked_が更新される()
    {
        var readerGuid = new Guid(GuidPrefix.Unknown, EntityId.Unknown);
        var proxy = new ReaderProxy(readerGuid);
        // bitmapBase=5 (= 4 まで ack 済み)、bit 0 set (= 5 を要求)
        var snSet = new SequenceNumberSet(new SequenceNumber(5L), 1, new uint[] { 0x80000000u });
        proxy.ProcessAckNack(snSet);

        proxy.HighestAcked.Value.Should().Be(4L);
        proxy.RequestedSequenceNumbers().Select(s => s.Value).Should().Equal(5L);
    }

    [Fact]
    public async Task Writer_は_history_に無い要求SNへ_GAP_を返す()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history);
        using (writer)
        {
            writer.MatchReader(readerGuid, s.ReaderLocator);

            var gapTcs = new TaskCompletionSource<GapSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.ReaderTransport.Received += (packet, _) =>
            {
                if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _))
                {
                    return;
                }

                var reader = new RtpsMessageReader(packet.Span);
                while (reader.TryReadNext(out var header, out var body))
                {
                    if (header.Kind != SubmessageKind.Gap)
                    {
                        continue;
                    }

                    gapTcs.TrySetResult(GapSubmessage.ReadBody(body, header.Endianness, header.Flags));
                }
            };

            var ackPacket = BuildAckNackPacket(
                s.ReaderPrefix,
                s.ReaderEntityId,
                s.WriterEntityId,
                new SequenceNumberSet(new SequenceNumber(1L), 1, new[] { 0x80000000u }));

            writer.ProcessPacket(ackPacket);

            var gap = await gapTcs.Task.WaitAsync(ReceiveTimeout);
            gap.ReaderEntityId.Should().Be(s.ReaderEntityId);
            gap.WriterEntityId.Should().Be(s.WriterEntityId);
            gap.GapStart.Value.Should().Be(1L);
            gap.GapList.BitmapBase.Value.Should().Be(2L);
            gap.GapList.NumBits.Should().Be(0);
        }
    }

    private static byte[] BuildAckNackPacket(
        GuidPrefix sourcePrefix,
        EntityId readerEntityId,
        EntityId writerEntityId,
        SequenceNumberSet readerSnState)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.Rclsharp, sourcePrefix);
        writer.WriteAckNack(new AckNackSubmessage(
            readerEntityId,
            writerEntityId,
            readerSnState,
            count: 1,
            final: false));
        return writer.WrittenSpan.ToArray();
    }
}
