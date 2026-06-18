using ROSettaDDS.Common;

namespace ROSettaDDS.Cdr.ParameterList;

/// <summary>
/// サービス reply の inline QoS に載る related_sample_identity (Fast DDS RPC) の組み立て・解析。
/// 書き込みは PID 0x800F、読み取りは 0x800F / 0x0083 (レガシ) の双方を受理する。
/// </summary>
public static class RelatedSampleIdentityInlineQos
{
    /// <summary>related_sample_identity を含む PL_CDR inline QoS (SENTINEL 込み) を生成する。</summary>
    public static byte[] Build(SampleIdentity identity, CdrEndianness endianness)
    {
        Span<byte> buffer = stackalloc byte[32]; // PID(2)+len(2)+24 + sentinel(4) = 32
        var writer = new CdrWriter(buffer, endianness);
        var pl = new ParameterListWriter(writer);
        pl.BeginParameter(ParameterId.RelatedSampleIdentity);
        Span<byte> idBytes = stackalloc byte[SampleIdentity.Size];
        identity.WriteTo(idBytes, endianness == CdrEndianness.LittleEndian);
        pl.WriteRawBytes(idBytes);
        pl.EndParameter();
        pl.WriteSentinel();

        var current = pl.CurrentWriter;
        var copy = new byte[current.Position];
        current.WrittenSpan.CopyTo(copy);
        return copy;
    }

    /// <summary>inline QoS から related_sample_identity を読み出す。</summary>
    public static bool TryRead(ReadOnlySpan<byte> inlineQos, CdrEndianness endianness, out SampleIdentity identity)
    {
        identity = default;
        if (inlineQos.IsEmpty)
        {
            return false;
        }
        var reader = new CdrReader(inlineQos, endianness);
        var pl = new ParameterListReader(reader);
        while (pl.MoveNext(out var pid, out _))
        {
            ushort stripped = ParameterId.StripFlags(pid);
            if (stripped != ParameterId.RelatedSampleIdentity && stripped != ParameterId.RelatedSampleIdentityLegacy)
            {
                continue;
            }
            var raw = pl.CurrentValueRaw();
            if (raw.Length < SampleIdentity.Size)
            {
                return false;
            }
            identity = SampleIdentity.Read(raw, endianness == CdrEndianness.LittleEndian);
            return true;
        }
        return false;
    }
}
