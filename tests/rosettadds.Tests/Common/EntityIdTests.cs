using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class EntityIdTests
{
    [Fact]
    public void Participant_は_0x000001c1()
    {
        EntityId.Participant.Value.Should().Be(0x0000_01C1u);
        EntityId.Participant.Kind.Should().Be(EntityKind.BuiltinParticipant);
        EntityId.Participant.Key.Should().Be(0x0000_01u);
    }

    [Fact]
    public void Unknown_は_0()
    {
        EntityId.Unknown.Value.Should().Be(0u);
    }

    [Fact]
    public void Key_と_Kind_から_value_が組み立てられる()
    {
        var id = new EntityId(0x0001_00, EntityKind.BuiltinWriterWithKey);
        id.Value.Should().Be(0x0001_00C2u);
        id.Key.Should().Be(0x0000_0100u);
        id.Kind.Should().Be(EntityKind.BuiltinWriterWithKey);
    }

    [Fact]
    public void Key_が_24bit_を超えると例外()
    {
        Action act = () => _ = new EntityId(0x0100_0000u, EntityKind.UserDefinedReaderWithKey);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsBuiltin_は_kind_の上位2bit_で判定される()
    {
        new EntityId(0u, EntityKind.BuiltinWriterWithKey).IsBuiltin.Should().BeTrue();
        new EntityId(0u, EntityKind.UserDefinedWriterWithKey).IsBuiltin.Should().BeFalse();
        EntityId.Participant.IsBuiltin.Should().BeTrue();
    }

    [Fact]
    public void WriteTo_と_Read_は_BigEndian_で往復する()
    {
        var src = new EntityId(0x0000_01C1u);
        var buf = new byte[4];
        src.WriteTo(buf);

        // ビッグエンディアン: 0x00, 0x00, 0x01, 0xC1
        buf.Should().Equal(0x00, 0x00, 0x01, 0xC1);

        var roundtrip = EntityId.Read(buf);
        roundtrip.Should().Be(src);
    }

    [Fact]
    public void 等価演算子()
    {
        var a = new EntityId(0x12345678u);
        var b = new EntityId(0x12345678u);
        var c = new EntityId(0x12345679u);

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
