using System.Diagnostics;
using System.Net;
using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Rtps;
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
    public void WriterProxy_の_BuildAckNackBitmap_は_最大256bit_の範囲で欠損を表す()
    {
        var proxy = new WriterProxy(new Guid(GuidPrefix.Unknown, EntityId.Unknown));
        proxy.UpdateHeartbeatRange(new SequenceNumber(1L), new SequenceNumber(3_000_000_000L));

        var snSet = proxy.BuildAckNackBitmap();

        snSet.BitmapBase.Value.Should().Be(1L);
        snSet.NumBits.Should().Be(256);
        snSet.IsSet(0).Should().BeTrue();
        snSet.IsSet(255).Should().BeTrue();
        snSet.IsSet(256).Should().BeFalse();
    }

    [Fact]
    public void WriterProxy_の_MarkGap_は_巨大範囲を展開しない()
    {
        var proxy = new WriterProxy(new Guid(GuidPrefix.Unknown, EntityId.Unknown));
        const long gapBitmapBase = 50_000_000L;
        proxy.UpdateHeartbeatRange(new SequenceNumber(1L), new SequenceNumber(500L));

        var elapsed = Stopwatch.StartNew();
        proxy.MarkGap(
            new SequenceNumber(100L),
            new SequenceNumberSet(new SequenceNumber(gapBitmapBase), 0, Array.Empty<uint>()));
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        proxy.HighestContiguousReceived.Value.Should().Be(0L);
        proxy.TrackedSatisfiedRangeCount.Should().Be(1);

        var snSet = proxy.BuildAckNackBitmap();
        snSet.BitmapBase.Value.Should().Be(1L);
        snSet.NumBits.Should().Be(256);
        snSet.IsSet(98).Should().BeTrue();   // 99 missing
        snSet.IsSet(99).Should().BeFalse();  // 100 gap
        snSet.IsSet(255).Should().BeFalse(); // 256 gap
    }

    [Fact]
    public void WriterProxy_は_連続受信済みSNを保持し続けない()
    {
        var proxy = new WriterProxy(new Guid(GuidPrefix.Unknown, EntityId.Unknown));
        const long receivedCount = 10_000L;
        proxy.UpdateHeartbeatRange(new SequenceNumber(1L), new SequenceNumber(receivedCount));

        for (long sn = 1L; sn <= receivedCount; sn++)
        {
            proxy.MarkReceived(new SequenceNumber(sn)).Should().BeTrue();
        }

        proxy.HighestContiguousReceived.Value.Should().Be(receivedCount);
        proxy.TrackedSatisfiedRangeCount.Should().Be(0);

        var snSet = proxy.BuildAckNackBitmap();
        snSet.BitmapBase.Value.Should().Be(receivedCount + 1);
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
    public void StatefulWriter_MatchReader_は既存readerのlocatorを更新する()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var firstLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.20"), 8000u);
        var updatedLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.21"), 8001u);
        var history = new WriterHistoryCache(writerGuid);
        using var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history);

        writer.MatchReader(readerGuid, firstLocator);
        writer.MatchReader(readerGuid, updatedLocator);

        writer.GetReaderProxy(readerGuid)!.UnicastLocator.Should().Be(updatedLocator);
    }

    [Fact]
    public void StatefulReader_MatchWriter_は既存writerのreply_locatorを更新する()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var firstLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.30"), 9000u);
        var updatedLocator = Locator.FromUdpV4(IPAddress.Parse("10.0.0.31"), 9001u);
        using var reader = new StatefulReader(
            replyTransport: s.ReaderTransport,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.ReaderPrefix,
            readerEntityId: s.ReaderEntityId,
            ackNackFallbackDestination: s.WriterLocator);

        reader.MatchWriter(writerGuid, firstLocator);
        reader.MatchWriter(writerGuid, updatedLocator);

        reader.GetWriterProxy(writerGuid)!.UnicastReplyLocator.Should().Be(updatedLocator);
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
            s.ReaderTransport.Received += (packet, remote) =>
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

    [Fact]
    public async Task DATA_FRAG_InlineQos_STATUS_INFO_を_完成fragment以外から反映する()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var reader = new StatefulReader(
            replyTransport: s.ReaderTransport,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.ReaderPrefix,
            readerEntityId: s.ReaderEntityId,
            ackNackFallbackDestination: s.WriterLocator);
        using (reader)
        {
            reader.MatchWriter(writerGuid, s.WriterLocator);
            var receivedTcs = new TaskCompletionSource<CacheChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.PayloadReceived += change => receivedTcs.TrySetResult(change);

            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var inlineQos = DataSubmessage.BuildStatusInfoInlineQos(
                DataSubmessage.StatusInfoUnregistered,
                CdrEndianness.LittleEndian);

            reader.ProcessPacket(BuildDataFragPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                new SequenceNumber(10),
                fragmentStartingNumber: 1,
                fragmentsInSubmessage: 1,
                fragmentSize: 4,
                sampleSize: (uint)payload.Length,
                fragmentPayload: payload.AsMemory(0, 4),
                inlineQos: inlineQos,
                keyPresent: true));
            reader.ProcessPacket(BuildDataFragPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                new SequenceNumber(10),
                fragmentStartingNumber: 2,
                fragmentsInSubmessage: 1,
                fragmentSize: 4,
                sampleSize: (uint)payload.Length,
                fragmentPayload: payload.AsMemory(4, 4)));

            var change = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
            change.Kind.Should().Be(ChangeKind.NotAliveUnregistered);
            change.SerializedPayload.ToArray().Should().Equal(payload);
        }
    }

    [Fact]
    public void Reader_は_GAP_を受けると欠損SNを解消する()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var reader = new StatefulReader(
            replyTransport: s.ReaderTransport,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.Rclsharp,
            localPrefix: s.ReaderPrefix,
            readerEntityId: s.ReaderEntityId,
            ackNackFallbackDestination: s.WriterLocator);
        using (reader)
        {
            reader.MatchWriter(writerGuid, s.WriterLocator);
            reader.ProcessPacket(BuildDataPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                new SequenceNumber(1),
                new byte[] { 1 }));
            reader.ProcessPacket(BuildDataPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                new SequenceNumber(3),
                new byte[] { 3 }));
            reader.ProcessPacket(BuildHeartbeatPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                first: new SequenceNumber(1),
                last: new SequenceNumber(5)));

            var proxy = reader.GetWriterProxy(writerGuid);
            proxy.Should().NotBeNull();
            proxy!.MissingSequenceNumbers().Select(sn => sn.Value).Should().Equal(2L, 4L, 5L);

            reader.ProcessPacket(BuildGapPacket(
                s.WriterPrefix,
                s.WriterEntityId,
                s.ReaderEntityId,
                gapStart: new SequenceNumber(2),
                gapList: new SequenceNumberSet(new SequenceNumber(3), 0, Array.Empty<uint>())));

            proxy.MissingSequenceNumbers().Select(sn => sn.Value).Should().Equal(4L, 5L);
            proxy.BuildAckNackBitmap().BitmapBase.Value.Should().Be(4L);
        }
    }

    [Fact]
    public async Task Writer_は_MTU超_payload_を_DATA_FRAG_で送信する()
    {
        var s = CreateSetup();
        var (writer, reader) = BuildPair(s, heartbeatPeriod: TimeSpan.FromMilliseconds(50));
        using (writer)
        using (reader)
        {
            int dataFragCount = 0;
            s.ReaderTransport.Received += (packet, _) =>
            {
                var rtpsReader = new RtpsMessageReader(packet.Span);
                while (rtpsReader.TryReadNext(out var header, out var ignoredBody))
                {
                    if (header.Kind == SubmessageKind.DataFrag)
                    {
                        Interlocked.Increment(ref dataFragCount);
                    }
                }
            };

            var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i & 0xFF)).ToArray();
            var receivedTcs = new TaskCompletionSource<CacheChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.PayloadReceived += change => receivedTcs.TrySetResult(change);

            await writer.WriteAsync(payload);

            var change = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
            change.SerializedPayload.ToArray().Should().Equal(payload);
            Volatile.Read(ref dataFragCount).Should().BeGreaterThan(1);
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

    private static byte[] BuildDataPacket(
        GuidPrefix sourcePrefix,
        EntityId writerEntityId,
        EntityId readerEntityId,
        SequenceNumber sequenceNumber,
        ReadOnlyMemory<byte> payload)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.Rclsharp, sourcePrefix);
        writer.WriteData(new DataSubmessage(
            readerEntityId,
            writerEntityId,
            sequenceNumber,
            payload));
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildHeartbeatPacket(
        GuidPrefix sourcePrefix,
        EntityId writerEntityId,
        EntityId readerEntityId,
        SequenceNumber first,
        SequenceNumber last)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.Rclsharp, sourcePrefix);
        writer.WriteHeartbeat(new HeartbeatSubmessage(
            readerEntityId,
            writerEntityId,
            first,
            last,
            count: 1,
            final: false));
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildGapPacket(
        GuidPrefix sourcePrefix,
        EntityId writerEntityId,
        EntityId readerEntityId,
        SequenceNumber gapStart,
        SequenceNumberSet gapList)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.Rclsharp, sourcePrefix);
        writer.WriteGap(new GapSubmessage(
            readerEntityId,
            writerEntityId,
            gapStart,
            gapList));
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] BuildDataFragPacket(
        GuidPrefix sourcePrefix,
        EntityId writerEntityId,
        EntityId readerEntityId,
        SequenceNumber sequenceNumber,
        uint fragmentStartingNumber,
        ushort fragmentsInSubmessage,
        ushort fragmentSize,
        uint sampleSize,
        ReadOnlyMemory<byte> fragmentPayload,
        ReadOnlyMemory<byte> inlineQos = default,
        bool keyPresent = false)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.Rclsharp, sourcePrefix);
        writer.WriteDataFrag(new DataFragSubmessage(
            readerEntityId,
            writerEntityId,
            sequenceNumber,
            fragmentStartingNumber,
            fragmentsInSubmessage,
            fragmentSize,
            sampleSize,
            fragmentPayload,
            inlineQos,
            keyPresent));
        return writer.WrittenSpan.ToArray();
    }
}
