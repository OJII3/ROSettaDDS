using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rtps.Reader;

internal sealed class DataFragReassemblyBuffer
{
    private readonly DataFragReassemblyOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<FragmentKey, PartialSample> _samples = new();
    private long _bufferedSampleBytes;

    public DataFragReassemblyBuffer(DataFragReassemblyOptions? options = null, Func<DateTime>? clock = null)
    {
        _options = options ?? DataFragReassemblyOptions.Default;
        _options.Validate();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public long BufferedSampleBytes => _bufferedSampleBytes;

    public DataFragReassemblyResult? Add(
        Guid writerGuid,
        DataFragSubmessage fragment,
        CdrEndianness endianness)
    {
        if (fragment.SampleSize > (uint)_options.MaxSampleSize)
        {
            return null;
        }
        int sampleSize = checked((int)fragment.SampleSize);
        if (sampleSize > _options.MaxBufferedBytes)
        {
            return null;
        }
        if (fragment.SerializedPayloadFragment.IsEmpty)
        {
            return null;
        }

        var now = _clock();
        RemoveExpired(now);

        var key = new FragmentKey(writerGuid, fragment.WriterSequenceNumber, fragment.SampleSize);
        if (!_samples.TryGetValue(key, out var sample))
        {
            EvictOldestUntilCapacityAvailable(sampleSize);
            sample = new PartialSample(fragment.SampleSize, fragment.FragmentSize, now);
            _samples.Add(key, sample);
            _bufferedSampleBytes += sample.Buffer.Length;
        }
        else if (sample.FragmentSize != fragment.FragmentSize)
        {
            RemoveSample(key);
            return null;
        }

        if (!sample.TryAdd(fragment, endianness, now))
        {
            return null;
        }
        if (!sample.IsComplete)
        {
            return null;
        }

        RemoveSample(key);
        return new DataFragReassemblyResult(sample.Buffer, sample.InlineQos, sample.InlineQosEndianness);
    }

    private void RemoveExpired(DateTime now)
    {
        if (_samples.Count == 0)
        {
            return;
        }

        foreach (var key in _samples
            .Where(pair => now - pair.Value.LastUpdated >= _options.TimeToLive)
            .Select(pair => pair.Key)
            .ToArray())
        {
            RemoveSample(key);
        }
    }

    private void EvictOldestUntilCapacityAvailable(int sampleSize)
    {
        while (_samples.Count >= _options.MaxBufferedSamples
            || _bufferedSampleBytes + sampleSize > _options.MaxBufferedBytes)
        {
            if (_samples.Count == 0)
            {
                return;
            }

            var oldestKey = _samples.Aggregate(
                (oldest, current) => current.Value.LastUpdated < oldest.Value.LastUpdated ? current : oldest).Key;
            RemoveSample(oldestKey);
        }
    }

    private void RemoveSample(FragmentKey key)
    {
        if (!_samples.Remove(key, out var sample))
        {
            return;
        }

        _bufferedSampleBytes -= sample.Buffer.Length;
    }

    private readonly struct FragmentKey : IEquatable<FragmentKey>
    {
        private readonly Guid _writerGuid;
        private readonly SequenceNumber _sequenceNumber;
        private readonly uint _sampleSize;

        public FragmentKey(Guid writerGuid, SequenceNumber sequenceNumber, uint sampleSize)
        {
            _writerGuid = writerGuid;
            _sequenceNumber = sequenceNumber;
            _sampleSize = sampleSize;
        }

        public bool Equals(FragmentKey other)
            => _writerGuid.Equals(other._writerGuid)
            && _sequenceNumber.Equals(other._sequenceNumber)
            && _sampleSize == other._sampleSize;

        public override bool Equals(object? obj) => obj is FragmentKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_writerGuid, _sequenceNumber, _sampleSize);
    }

    private sealed class PartialSample
    {
        private readonly bool[] _receivedFragments;
        private int _receivedCount;

        public PartialSample(uint sampleSize, ushort fragmentSize, DateTime createdAt)
        {
            Buffer = new byte[checked((int)sampleSize)];
            FragmentSize = fragmentSize;
            LastUpdated = createdAt;

            int fragmentCount = checked((int)((sampleSize + fragmentSize - 1u) / fragmentSize));
            _receivedFragments = new bool[fragmentCount];
        }

        public byte[] Buffer { get; }
        public ushort FragmentSize { get; }
        public ReadOnlyMemory<byte> InlineQos { get; private set; }
        public CdrEndianness InlineQosEndianness { get; private set; } = CdrEndianness.LittleEndian;
        public DateTime LastUpdated { get; private set; }
        public bool IsComplete => _receivedCount == _receivedFragments.Length;

        public bool TryAdd(DataFragSubmessage fragment, CdrEndianness endianness, DateTime updatedAt)
        {
            var payload = fragment.SerializedPayloadFragment.Span;
            int expectedPayloadLength = 0;
            for (int i = 0; i < fragment.FragmentsInSubmessage; i++)
            {
                uint fragmentNumber = fragment.FragmentStartingNumber + (uint)i;
                if (fragmentNumber == 0)
                {
                    return false;
                }

                int fragmentIndex = checked((int)fragmentNumber) - 1;
                if (fragmentIndex < 0 || fragmentIndex >= _receivedFragments.Length)
                {
                    return false;
                }

                int destinationOffset = checked(fragmentIndex * FragmentSize);
                int expectedLength = Math.Min(FragmentSize, Buffer.Length - destinationOffset);
                if (expectedLength <= 0)
                {
                    return false;
                }
                expectedPayloadLength += expectedLength;
            }

            if (expectedPayloadLength != payload.Length)
            {
                return false;
            }

            int payloadOffset = 0;
            for (int i = 0; i < fragment.FragmentsInSubmessage; i++)
            {
                uint fragmentNumber = fragment.FragmentStartingNumber + (uint)i;
                int fragmentIndex = checked((int)fragmentNumber) - 1;
                int destinationOffset = checked(fragmentIndex * FragmentSize);
                int expectedLength = Math.Min(FragmentSize, Buffer.Length - destinationOffset);
                payload.Slice(payloadOffset, expectedLength).CopyTo(Buffer.AsSpan(destinationOffset, expectedLength));
                payloadOffset += expectedLength;

                if (!_receivedFragments[fragmentIndex])
                {
                    _receivedFragments[fragmentIndex] = true;
                    _receivedCount++;
                }
            }

            if (!fragment.InlineQos.IsEmpty && InlineQos.IsEmpty)
            {
                InlineQos = fragment.InlineQos.ToArray();
                InlineQosEndianness = endianness;
            }

            LastUpdated = updatedAt;
            return true;
        }
    }
}
