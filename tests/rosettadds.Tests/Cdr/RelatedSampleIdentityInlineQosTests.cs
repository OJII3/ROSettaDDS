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
}
