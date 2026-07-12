using System.Diagnostics;
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Submessages;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class StatefulWriterPacketContractTests
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
            WriterPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x01),
            ReaderPrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11, 0x22, 0x02),
            WriterEntityId = new EntityId(0x0000_0001u, EntityKind.UserDefinedWriterNoKey),
            ReaderEntityId = new EntityId(0x0000_0002u, EntityKind.UserDefinedReaderNoKey),
        };
    }

    private static StatefulWriter CreateWriter(Setup s, out WriterHistoryCache history)
    {
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        history = new WriterHistoryCache(writerGuid);
        return new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history);
    }

    [Fact]
    public async Task 空historyのHEARTBEATはfirstSN1_lastSN0()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        writer.MatchReader(readerGuid, s.ReaderLocator);

        var hbTcs = new TaskCompletionSource<HeartbeatSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Heartbeat)
                    hbTcs.TrySetResult(HeartbeatSubmessage.ReadBody(body, header.Endianness, header.Flags));
            }
        };

        writer.Start();
        var hb = await hbTcs.Task.WaitAsync(ReceiveTimeout);
        hb.FirstSequenceNumber.Value.Should().Be(1L);
        hb.LastSequenceNumber.Value.Should().Be(0L);
    }

    [Fact]
    public async Task Alive_DATAのpacket構築が不変()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out _);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        writer.MatchReader(readerGuid, s.ReaderLocator);

        var dataTcs = new TaskCompletionSource<DataSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Data)
                    dataTcs.TrySetResult(DataSubmessage.ReadBody(body, header.Endianness, header.Flags));
            }
        };

        await writer.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
        var data = await dataTcs.Task.WaitAsync(ReceiveTimeout);
        data.WriterSequenceNumber.Value.Should().Be(1L);
        data.SerializedPayload.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task pre_join_SNのNACKにGAPを返す()
    {
        var s = CreateSetup();
        using var writer = CreateWriter(s, out var history);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);

        for (int i = 1; i <= 3; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }
        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

        int gapCount = 0;
        var gapTcs = new TaskCompletionSource<GapSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReaderTransport.Received += (packet, source) =>
        {
            if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _)) return;
            var reader = new RtpsMessageReader(packet.Span);
            while (reader.TryReadNext(out var header, out var body))
            {
                if (header.Kind == SubmessageKind.Gap)
                {
                    Interlocked.Increment(ref gapCount);
                    gapTcs.TrySetResult(GapSubmessage.ReadBody(body, header.Endianness, header.Flags));
                }
            }
        };

        var ackPacket = BuildAckNackPacket(s.ReaderPrefix, s.ReaderEntityId, s.WriterEntityId,
            new SequenceNumberSet(new SequenceNumber(1L), 1, new[] { 0x80000000u }));
        writer.ProcessPacket(ackPacket);

        var gap = await gapTcs.Task.WaitAsync(ReceiveTimeout);
        gap.GapStart.Value.Should().Be(1L);
        gap.ReaderEntityId.Should().Be(s.ReaderEntityId);
        gap.WriterEntityId.Should().Be(s.WriterEntityId);
    }

    private static byte[] BuildAckNackPacket(
        GuidPrefix readerPrefix, EntityId readerEntityId, EntityId writerEntityId,
        SequenceNumberSet snSet)
    {
        var buffer = new byte[1500];
        var w = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.ROSettaDDS, readerPrefix);
        w.WriteAckNack(new AckNackSubmessage(readerEntityId, writerEntityId, snSet, count: 1, final: false));
        var packet = new byte[w.BytesWritten];
        w.WrittenSpan.CopyTo(packet);
        return packet;
    }
}
