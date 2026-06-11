using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class DataFragReassemblyBufferTests
{
    private static readonly Guid WriterGuid = new(
        GuidPrefix.Create(VendorId.ROSettaDDS, 0x10, 0x20, 0x30),
        new EntityId(0x000005u, EntityKind.UserDefinedWriterNoKey));

    [Fact]
    public void 順不同_fragment_と_複数fragment_submessage_を再構成できる()
    {
        var buffer = new DataFragReassemblyBuffer();
        var sample = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        var secondAndThird = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 1, start: 2, count: 2, fragmentSize: 4, sample, payloadOffset: 4, payloadLength: 6),
            CdrEndianness.LittleEndian);
        secondAndThird.Should().BeNull();

        var completed = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 1, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian);

        completed.Should().NotBeNull();
        completed!.Value.Payload.Should().Equal(sample);
        buffer.BufferedSampleBytes.Should().Be(0);
    }

    [Fact]
    public void InlineQos_は_完成させたfragment以外からも保持される()
    {
        var buffer = new DataFragReassemblyBuffer();
        var sample = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
        var inlineQos = DataSubmessage.BuildStatusInfoInlineQos(
            DataSubmessage.StatusInfoUnregistered,
            CdrEndianness.LittleEndian);

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 2, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4, inlineQos: inlineQos),
            CdrEndianness.LittleEndian).Should().BeNull();

        var completed = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 2, start: 2, count: 1, fragmentSize: 4, sample, payloadOffset: 4, payloadLength: 4),
            CdrEndianness.LittleEndian);

        completed.Should().NotBeNull();
        completed!.Value.Payload.Should().Equal(sample);
        completed.Value.InlineQos.ToArray().Should().Equal(inlineQos);
        completed.Value.InlineQosEndianness.Should().Be(CdrEndianness.LittleEndian);
    }

    [Fact]
    public void FragmentSize_が途中で変わったsampleは破棄される()
    {
        var buffer = new DataFragReassemblyBuffer();
        var sample = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 3, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 3, start: 2, count: 1, fragmentSize: 8, sample, payloadOffset: 4, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();
        buffer.BufferedSampleBytes.Should().Be(0);

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 3, start: 2, count: 1, fragmentSize: 4, sample, payloadOffset: 4, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();

        var completed = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 3, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian);
        completed.Should().NotBeNull();
        completed!.Value.Payload.Should().Equal(sample);
    }

    [Fact]
    public void TTL_を過ぎた未完成sampleは破棄される()
    {
        var now = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var buffer = new DataFragReassemblyBuffer(
            new DataFragReassemblyOptions { TimeToLive = TimeSpan.FromMilliseconds(10) },
            () => now);
        var sample = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 4, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();

        now += TimeSpan.FromMilliseconds(11);
        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 4, start: 2, count: 1, fragmentSize: 4, sample, payloadOffset: 4, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();

        var completed = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 4, start: 1, count: 1, fragmentSize: 4, sample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian);
        completed.Should().NotBeNull();
        completed!.Value.Payload.Should().Equal(sample);
    }

    [Fact]
    public void MaxBufferedSamples_を超えると古いsampleを破棄する()
    {
        var buffer = new DataFragReassemblyBuffer(
            new DataFragReassemblyOptions { MaxBufferedSamples = 1 });
        var firstSample = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
        var secondSample = Enumerable.Range(10, 8).Select(i => (byte)i).ToArray();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 5, start: 1, count: 1, fragmentSize: 4, firstSample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();
        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 6, start: 1, count: 1, fragmentSize: 4, secondSample, payloadOffset: 0, payloadLength: 4),
            CdrEndianness.LittleEndian).Should().BeNull();

        var completed = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 6, start: 2, count: 1, fragmentSize: 4, secondSample, payloadOffset: 4, payloadLength: 4),
            CdrEndianness.LittleEndian);

        completed.Should().NotBeNull();
        completed!.Value.Payload.Should().Equal(secondSample);
        buffer.BufferedSampleBytes.Should().Be(0);
    }

    [Fact]
    public void MaxBufferedBytes_を超えるsampleは確保しない()
    {
        var buffer = new DataFragReassemblyBuffer(
            new DataFragReassemblyOptions { MaxSampleSize = 16, MaxBufferedBytes = 8 });
        var sample = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 7, start: 1, count: 1, fragmentSize: 6, sample, payloadOffset: 0, payloadLength: 6),
            CdrEndianness.LittleEndian).Should().BeNull();

        buffer.BufferedSampleBytes.Should().Be(0);
    }

    [Fact]
    public void MaxBufferedBytes_上限に近い場合は古いsampleを破棄してから確保する()
    {
        var now = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var buffer = new DataFragReassemblyBuffer(
            new DataFragReassemblyOptions
            {
                MaxSampleSize = 16,
                MaxBufferedBytes = 20,
                MaxBufferedSamples = 10,
            },
            () => now);
        var firstSample = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var secondSample = Enumerable.Range(20, 12).Select(i => (byte)i).ToArray();

        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 8, start: 1, count: 1, fragmentSize: 6, firstSample, payloadOffset: 0, payloadLength: 6),
            CdrEndianness.LittleEndian).Should().BeNull();
        buffer.BufferedSampleBytes.Should().Be(12);

        now += TimeSpan.FromMilliseconds(1);
        buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 9, start: 1, count: 1, fragmentSize: 6, secondSample, payloadOffset: 0, payloadLength: 6),
            CdrEndianness.LittleEndian).Should().BeNull();
        buffer.BufferedSampleBytes.Should().BeLessThanOrEqualTo(20);

        var firstCompleted = buffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 8, start: 2, count: 1, fragmentSize: 6, firstSample, payloadOffset: 6, payloadLength: 6),
            CdrEndianness.LittleEndian);

        firstCompleted.Should().BeNull();
        buffer.BufferedSampleBytes.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void SampleSize_上限超過と_payload_length不一致は完成しない()
    {
        var smallBuffer = new DataFragReassemblyBuffer(
            new DataFragReassemblyOptions { MaxSampleSize = 4 });
        var oversizedSample = Enumerable.Range(0, 5).Select(i => (byte)i).ToArray();

        smallBuffer.Add(
            WriterGuid,
            Fragment(sequenceNumber: 7, start: 1, count: 1, fragmentSize: 5, oversizedSample, payloadOffset: 0, payloadLength: 5),
            CdrEndianness.LittleEndian).Should().BeNull();

        var buffer = new DataFragReassemblyBuffer();
        var sample = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
        buffer.Add(
            WriterGuid,
            new DataFragSubmessage(
                EntityId.Unknown,
                WriterGuid.EntityId,
                new SequenceNumber(8),
                fragmentStartingNumber: 1,
                fragmentsInSubmessage: 2,
                fragmentSize: 4,
                sampleSize: 8,
                serializedPayloadFragment: sample.AsMemory(0, 4)),
            CdrEndianness.LittleEndian).Should().BeNull();
    }

    private static DataFragSubmessage Fragment(
        long sequenceNumber,
        uint start,
        ushort count,
        ushort fragmentSize,
        byte[] sample,
        int payloadOffset,
        int payloadLength,
        ReadOnlyMemory<byte> inlineQos = default)
        => new(
            EntityId.Unknown,
            WriterGuid.EntityId,
            new SequenceNumber(sequenceNumber),
            fragmentStartingNumber: start,
            fragmentsInSubmessage: count,
            fragmentSize: fragmentSize,
            sampleSize: (uint)sample.Length,
            serializedPayloadFragment: sample.AsMemory(payloadOffset, payloadLength),
            inlineQos: inlineQos);
}
