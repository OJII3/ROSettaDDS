using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Submessages;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Cdr;

public class RelatedSampleIdentityInlineQosTests
{
    private static SampleIdentity Sample()
    {
        var guid = new Guid(
            GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS),
            new EntityId(0x05u, EntityKind.UserDefinedWriterNoKey));
        return new SampleIdentity(guid, new SequenceNumber(7));
    }

    [Fact]
    public void Build_した_inlineQos_は_TryRead_で復元できる()
    {
        var id = Sample();
        var inlineQos = RelatedSampleIdentityInlineQos.Build(id, CdrEndianness.LittleEndian);

        RelatedSampleIdentityInlineQos.TryRead(inlineQos, CdrEndianness.LittleEndian, out var read)
            .Should().BeTrue();
        read.Should().Be(id);
    }

    [Fact]
    public void TryRead_は_related_identity_が無ければ_false()
    {
        var statusOnly = DataSubmessage.BuildStatusInfoInlineQos(0u, CdrEndianness.LittleEndian);

        RelatedSampleIdentityInlineQos.TryRead(statusOnly, CdrEndianness.LittleEndian, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Build_した_inlineQos_は_BigEndian_でも往復する()
    {
        var id = Sample();
        var inlineQos = RelatedSampleIdentityInlineQos.Build(id, CdrEndianness.BigEndian);

        RelatedSampleIdentityInlineQos.TryRead(inlineQos, CdrEndianness.BigEndian, out var read)
            .Should().BeTrue();
        read.Should().Be(id);
    }

    [Fact]
    public void TryRead_は_legacy_PID_0x0083_を受理する()
    {
        var id = Sample();

        // legacy PID (0x0083) で related_sample_identity を手組みする
        var buffer = new byte[40];
        var writer = new CdrWriter(buffer, CdrEndianness.LittleEndian);
        var pl = new ParameterListWriter(writer);
        pl.BeginParameter(ParameterId.RelatedSampleIdentityLegacy);
        var idBytes = new byte[SampleIdentity.Size];
        id.WriteTo(idBytes, littleEndian: true);
        pl.WriteRawBytes(idBytes);
        pl.EndParameter();
        pl.WriteSentinel();
        var current = pl.CurrentWriter;
        var inlineQos = current.WrittenSpan.Slice(0, current.Position).ToArray();

        RelatedSampleIdentityInlineQos.TryRead(inlineQos, CdrEndianness.LittleEndian, out var read)
            .Should().BeTrue();
        read.Should().Be(id);
    }
}
