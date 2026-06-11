using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;

namespace ROSettaDDS.Tests.Cdr;

public class ParameterListTests
{
    [Fact]
    public void ParameterId_の主要定数を確認()
    {
        ParameterId.Sentinel.Should().Be((ushort)0x0001);
        ParameterId.ProtocolVersion.Should().Be((ushort)0x0015);
        ParameterId.VendorId.Should().Be((ushort)0x0016);
        ParameterId.ParticipantGuid.Should().Be((ushort)0x0050);
        ParameterId.BuiltinEndpointSet.Should().Be((ushort)0x0058);
        ParameterId.DomainTag.Should().Be((ushort)0x4014);
        ParameterId.StripFlags(ParameterId.DomainTag).Should().Be(ParameterId.DomainTagBase);
        ParameterId.KeyHash.Should().Be((ushort)0x0070);
    }

    [Fact]
    public void IsVendorSpecific_と_IsMustUnderstand()
    {
        ParameterId.IsVendorSpecific(0x8000).Should().BeTrue();
        ParameterId.IsVendorSpecific(0x7FFF).Should().BeFalse();
        ParameterId.IsMustUnderstand(0x4015).Should().BeTrue();
        ParameterId.IsMustUnderstand(0x0015).Should().BeFalse();
        ParameterId.StripFlags(0x4015).Should().Be((ushort)0x0015);
        ParameterId.StripFlags(0x8015).Should().Be((ushort)0x8015);
        ParameterId.StripFlags(0xC015).Should().Be((ushort)0x8015);
    }

    [Fact]
    public void ThrowIfUnknownMustUnderstand_は_must_understand_PID_だけ拒否する()
    {
        var skipUnknown = () => ParameterId.ThrowIfUnknownMustUnderstand(0x0242);
        skipUnknown.Should().NotThrow();

        var rejectUnknownMustUnderstand = () => ParameterId.ThrowIfUnknownMustUnderstand(0x4242);
        rejectUnknownMustUnderstand.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*0x4242*");
    }

    [Fact]
    public void ParameterList_LE_bit_exact_PROTOCOL_VERSION_と_SENTINEL()
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var pl = new ParameterListWriter(w);
        pl.BeginParameter(ParameterId.ProtocolVersion);
        pl.WriteByte(2);
        pl.WriteByte(4);
        pl.EndParameter();
        pl.WriteSentinel();

        var written = pl.CurrentWriter.WrittenSpan.ToArray();
        // PID=0x0015 LE = [15, 00], length=4 LE = [04, 00], value=[02, 04, 00, 00] (2B + 2B pad)
        // SENTINEL: PID=0x01 LE = [01, 00], length=0 LE = [00, 00]
        written.Should().Equal(
            0x15, 0x00, 0x04, 0x00,
            0x02, 0x04, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void ParameterList_往復_複数パラメータ()
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var plw = new ParameterListWriter(w);

        plw.BeginParameter(ParameterId.ProtocolVersion);
        plw.WriteByte(2);
        plw.WriteByte(4);
        plw.EndParameter();

        plw.BeginParameter(ParameterId.VendorId);
        plw.WriteByte(0x01);
        plw.WriteByte(0x0F);
        plw.EndParameter();

        plw.BeginParameter(ParameterId.EntityName);
        plw.WriteString("rosettadds_node");
        plw.EndParameter();

        plw.BeginParameter(ParameterId.ParticipantLeaseDuration);
        plw.WriteInt32(3); // seconds
        plw.WriteUInt32(0); // fraction
        plw.EndParameter();

        plw.WriteSentinel();

        var totalLength = plw.CurrentWriter.BytesWritten;
        var serialized = buf[..totalLength].ToArray();

        // 読み出し
        var r = new CdrReader(serialized, CdrEndianness.LittleEndian);
        var plr = new ParameterListReader(r);

        // 1: PROTOCOL_VERSION
        plr.MoveNext(out var pid1, out var len1).Should().BeTrue();
        pid1.Should().Be(ParameterId.ProtocolVersion);
        len1.Should().Be((ushort)4);
        plr.ReadByte().Should().Be((byte)2);
        plr.ReadByte().Should().Be((byte)4);

        // 2: VENDORID
        plr.MoveNext(out var pid2, out var len2).Should().BeTrue();
        pid2.Should().Be(ParameterId.VendorId);
        len2.Should().Be((ushort)4);
        plr.ReadByte().Should().Be((byte)0x01);
        plr.ReadByte().Should().Be((byte)0x0F);

        // 3: ENTITY_NAME (string)
        plr.MoveNext(out var pid3, out _).Should().BeTrue();
        pid3.Should().Be(ParameterId.EntityName);
        plr.ReadString().Should().Be("rosettadds_node");

        // 4: PARTICIPANT_LEASE_DURATION (skipped value)
        plr.MoveNext(out var pid4, out var len4).Should().BeTrue();
        pid4.Should().Be(ParameterId.ParticipantLeaseDuration);
        len4.Should().Be((ushort)8);
        // 値を読まずに次へ
        plr.MoveNext(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void EndParameter_未呼び出しで_BeginParameter_は_例外()
    {
        Span<byte> buf = stackalloc byte[64];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var pl = new ParameterListWriter(w);
        pl.BeginParameter(ParameterId.ProtocolVersion);
        try
        {
            pl.BeginParameter(ParameterId.VendorId);
            Assert.Fail("should have thrown");
        }
        catch (InvalidOperationException) { /* expected */ }
    }

    [Fact]
    public void WriteSentinel_LE_bit_exact()
    {
        Span<byte> buf = stackalloc byte[8];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var pl = new ParameterListWriter(w);
        pl.WriteSentinel();

        pl.CurrentWriter.WrittenSpan.ToArray().Should().Equal(0x01, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void CurrentValueRaw_は_値領域の_生バイトを返す()
    {
        Span<byte> buf = stackalloc byte[32];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var plw = new ParameterListWriter(w);
        plw.BeginParameter(ParameterId.ProtocolVersion);
        plw.WriteByte(2);
        plw.WriteByte(4);
        plw.EndParameter();
        plw.WriteSentinel();
        var serialized = buf[..plw.CurrentWriter.BytesWritten].ToArray();

        var r = new CdrReader(serialized, CdrEndianness.LittleEndian);
        var plr = new ParameterListReader(r);
        plr.MoveNext(out _, out _);
        plr.CurrentValueRaw().ToArray().Should().Equal(0x02, 0x04, 0x00, 0x00);
    }
}
