using ROSettaDDS.Common;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class StatelessReaderDataFragTests
{
    private static readonly GuidPrefix SourcePrefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x21, 0x22, 0x23);
    private static readonly EntityId WriterEntityId = new(0x000005u, EntityKind.UserDefinedWriterNoKey);
    private static readonly EntityId ReaderEntityId = new(0x000005u, EntityKind.UserDefinedReaderNoKey);
    private static readonly Guid WriterGuid = new(SourcePrefix, WriterEntityId);

    [Fact]
    public void 未matchで完成したDATA_FRAGは_match後にsequence順で配送される()
    {
        var reader = new StatelessReader(ReaderEntityId);
        var received = new List<byte[]>();
        reader.PayloadReceived += (payload, _) => received.Add(payload.ToArray());

        var first = new byte[] { 1, 1, 1, 1, 1, 1 };
        var second = new byte[] { 2, 2, 2, 2, 2, 2 };

        ProcessTwoFragments(reader, sequenceNumber: 2, second);
        ProcessTwoFragments(reader, sequenceNumber: 1, first);
        received.Should().BeEmpty();

        reader.MatchWriter(WriterGuid);

        received.Should().HaveCount(2);
        received[0].Should().Equal(first);
        received[1].Should().Equal(second);
    }

    [Fact]
    public void 完成済みsequenceのDATA_FRAG再送は重複配送しない()
    {
        var reader = new StatelessReader(ReaderEntityId);
        reader.MatchWriter(WriterGuid);
        var received = new List<byte[]>();
        reader.PayloadReceived += (payload, _) => received.Add(payload.ToArray());
        var payload = new byte[] { 9, 8, 7, 6, 5, 4 };

        ProcessTwoFragments(reader, sequenceNumber: 3, payload);
        ProcessTwoFragments(reader, sequenceNumber: 3, payload);

        received.Should().ContainSingle();
        received[0].Should().Equal(payload);
    }

    [Fact]
    public void readerEntityId_不一致のDATA_FRAGは無視する()
    {
        var reader = new StatelessReader(ReaderEntityId);
        reader.MatchWriter(WriterGuid);
        bool received = false;
        reader.PayloadReceived += (_, _) => received = true;

        var otherReader = new EntityId(0x000006u, EntityKind.UserDefinedReaderNoKey);
        var payload = new byte[] { 1, 2, 3, 4 };
        reader.ProcessPacket(BuildDataFragPacket(
            readerId: otherReader,
            sequenceNumber: 4,
            fragmentStartingNumber: 1,
            fragmentsInSubmessage: 1,
            fragmentSize: 4,
            sampleSize: 4,
            fragmentPayload: payload));

        received.Should().BeFalse();
    }

    private static void ProcessTwoFragments(StatelessReader reader, long sequenceNumber, byte[] payload)
    {
        const ushort fragmentSize = 3;
        reader.ProcessPacket(BuildDataFragPacket(
            readerId: ReaderEntityId,
            sequenceNumber: sequenceNumber,
            fragmentStartingNumber: 2,
            fragmentsInSubmessage: 1,
            fragmentSize: fragmentSize,
            sampleSize: (uint)payload.Length,
            fragmentPayload: payload.AsMemory(3, 3)));
        reader.ProcessPacket(BuildDataFragPacket(
            readerId: ReaderEntityId,
            sequenceNumber: sequenceNumber,
            fragmentStartingNumber: 1,
            fragmentsInSubmessage: 1,
            fragmentSize: fragmentSize,
            sampleSize: (uint)payload.Length,
            fragmentPayload: payload.AsMemory(0, 3)));
    }

    private static byte[] BuildDataFragPacket(
        EntityId readerId,
        long sequenceNumber,
        uint fragmentStartingNumber,
        ushort fragmentsInSubmessage,
        ushort fragmentSize,
        uint sampleSize,
        ReadOnlyMemory<byte> fragmentPayload)
    {
        var buffer = new byte[1500];
        var writer = new RtpsMessageWriter(buffer, ProtocolVersion.V2_4, VendorId.ROSettaDDS, SourcePrefix);
        writer.WriteDataFrag(new DataFragSubmessage(
            readerEntityId: readerId,
            writerEntityId: WriterEntityId,
            writerSn: new SequenceNumber(sequenceNumber),
            fragmentStartingNumber: fragmentStartingNumber,
            fragmentsInSubmessage: fragmentsInSubmessage,
            fragmentSize: fragmentSize,
            sampleSize: sampleSize,
            serializedPayloadFragment: fragmentPayload));
        return writer.WrittenSpan.ToArray();
    }
}
