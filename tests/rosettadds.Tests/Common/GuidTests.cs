using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Common;

public class GuidTests
{
    [Fact]
    public void Size_は_16()
    {
        Guid.Size.Should().Be(16);
    }

    [Fact]
    public void Unknown_は_全0()
    {
        var g = Guid.Unknown;
        var buf = new byte[16];
        g.WriteTo(buf);
        buf.Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void WriteTo_と_Read_で往復する()
    {
        var prefix = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var entity = new EntityId(0x0000_01C1u);
        var src = new Guid(prefix, entity);

        var buf = new byte[16];
        src.WriteTo(buf);
        var roundtrip = Guid.Read(buf);

        roundtrip.Should().Be(src);
        roundtrip.Prefix.Should().Be(prefix);
        roundtrip.EntityId.Should().Be(entity);
    }

    [Fact]
    public void ToString_は_prefix_と_entityId_を_ドットで結合する()
    {
        var prefix = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var entity = new EntityId(0x0000_01C1u);
        var g = new Guid(prefix, entity);

        g.ToString().Should().Be($"{prefix}.000001C1");
    }
}
