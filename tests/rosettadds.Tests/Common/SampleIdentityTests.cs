using FluentAssertions;
using ROSettaDDS.Common;
using Xunit;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Common;

public class SampleIdentityTests
{
    private static Guid SampleGuid()
        => new Guid(
            GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS),
            new EntityId(0x05u, EntityKind.UserDefinedWriterNoKey));

    [Fact]
    public void WriteTo_と_Read_は往復する_LE()
    {
        var id = new SampleIdentity(SampleGuid(), new SequenceNumber(42));

        Span<byte> buf = stackalloc byte[SampleIdentity.Size];
        id.WriteTo(buf, littleEndian: true);
        var read = SampleIdentity.Read(buf, littleEndian: true);

        read.Should().Be(id);
    }

    [Fact]
    public void Size_は_24バイト()
    {
        SampleIdentity.Size.Should().Be(24);
    }
}
