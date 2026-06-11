using ROSettaDDS.Cdr;

namespace ROSettaDDS.Rtps.Reader;

internal readonly struct DataFragReassemblyResult
{
    public DataFragReassemblyResult(
        byte[] payload,
        ReadOnlyMemory<byte> inlineQos,
        CdrEndianness inlineQosEndianness)
    {
        Payload = payload;
        InlineQos = inlineQos;
        InlineQosEndianness = inlineQosEndianness;
    }

    public byte[] Payload { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public CdrEndianness InlineQosEndianness { get; }
}
