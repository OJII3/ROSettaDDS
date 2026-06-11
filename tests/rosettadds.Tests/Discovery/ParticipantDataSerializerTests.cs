using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using ROSettaDDS.Discovery;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Discovery;

public class ParticipantDataSerializerTests
{
    private static ParticipantData MakeSampleData()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 0x11223344, 0x55667788, 0x99AA);
        var data = new ParticipantData
        {
            ProtocolVersion = ProtocolVersion.V2_4,
            VendorId = VendorId.ROSettaDDS,
            Guid = new Guid(prefix, BuiltinEntityIds.Participant),
            BuiltinEndpoints = BuiltinEndpointSet.ROSettaDDSDefault,
            LeaseDuration = Duration.FromSeconds(20),
            ExpectsInlineQos = false,
            EntityName = "rosettadds_test_node",
        };
        data.MetatrafficUnicastLocators.Add(Locator.FromUdpV4(IPAddress.Parse("192.168.1.10"), 7411u));
        data.MetatrafficMulticastLocators.Add(Locator.FromUdpV4(IPAddress.Parse("239.255.0.1"), 7400u));
        return data;
    }

    private static ParticipantData ReadParticipantData(
        byte[] buffer,
        int length,
        DiscoveryLimits? limits = null)
    {
        var r = new CdrReader(buffer.AsSpan(0, length), CdrEndianness.LittleEndian);
        return ParticipantDataSerializer.Read(ref r, limits);
    }

    [Fact]
    public void 全フィールドの_PL_CDR_往復()
    {
        var src = MakeSampleData();
        var buf = new byte[1024];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, src);
        int written = w.Position;

        var r = new CdrReader(buf.AsSpan(0, written), CdrEndianness.LittleEndian);
        var read = ParticipantDataSerializer.Read(ref r);

        read.ProtocolVersion.Should().Be(src.ProtocolVersion);
        read.VendorId.Should().Be(src.VendorId);
        read.Guid.Should().Be(src.Guid);
        read.BuiltinEndpoints.Should().Be(src.BuiltinEndpoints);
        read.LeaseDuration.Should().Be(src.LeaseDuration);
        read.ExpectsInlineQos.Should().Be(src.ExpectsInlineQos);
        read.EntityName.Should().Be(src.EntityName);
        read.MetatrafficUnicastLocators.Should().Equal(src.MetatrafficUnicastLocators);
        read.MetatrafficMulticastLocators.Should().Equal(src.MetatrafficMulticastLocators);
    }

    [Fact]
    public void encap_PL_CDR_LE_を含む完全な_serializedPayload_を構築()
    {
        var src = MakeSampleData();
        var buf = new byte[1024];

        // 4B encap header + PL_CDR
        CdrEncapsulation.Write(buf, CdrEncapsulation.PlCdrLittleEndian);
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        ParticipantDataSerializer.Write(ref w, src);
        int total = w.Position;

        // 先頭 4B が encap header (BE 解釈で 0x0003)
        buf[0].Should().Be((byte)0x00);
        buf[1].Should().Be((byte)0x03); // PL_CDR_LE
        buf[2].Should().Be((byte)0x00); // options
        buf[3].Should().Be((byte)0x00);

        // 続いて最初の PID は 0x0015 (PROTOCOL_VERSION) LE = [0x15, 0x00]
        buf[4].Should().Be((byte)0x15);
        buf[5].Should().Be((byte)0x00);
        buf[6].Should().Be((byte)0x04); // length=4 LE
        buf[7].Should().Be((byte)0x00);

        // Round trip 全体
        var (kind, _) = CdrEncapsulation.Read(buf.AsSpan(0, 4));
        kind.Should().Be(CdrEncapsulation.PlCdrLittleEndian);
        var endian = CdrEncapsulation.GetEndianness(kind);
        var r = new CdrReader(buf.AsSpan(0, total), endian, cdrOrigin: CdrEncapsulation.Size);
        var read = ParticipantDataSerializer.Read(ref r);
        read.Guid.Should().Be(src.Guid);
        read.EntityName.Should().Be(src.EntityName);
    }

    [Fact]
    public void 未知_PID_はスキップされる()
    {
        // PROTOCOL_VERSION の前に must-understand ではない未知 PID 0x0242 (length 4) を挟んで構築
        var buf = new byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        // unknown PID
        w.WriteUInt16(0x0242);
        w.WriteUInt16(4);
        w.WriteUInt32(0xDEADBEEFu);
        // PROTOCOL_VERSION
        w.WriteUInt16(0x0015);
        w.WriteUInt16(4);
        w.WriteByte(2);
        w.WriteByte(7);
        w.WriteByte(0);
        w.WriteByte(0);
        // SENTINEL
        w.WriteUInt16(0x0001);
        w.WriteUInt16(0);
        int total = w.Position;

        var r = new CdrReader(buf.AsSpan(0, total), CdrEndianness.LittleEndian);
        var data = ParticipantDataSerializer.Read(ref r);
        data.ProtocolVersion.Should().Be(new ProtocolVersion(2, 7));
    }

    [Fact]
    public void vendor_specific_PID_は_標準_PID_として解釈されない()
    {
        var buf = new byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);

        w.WriteUInt16(ParameterId.ProtocolVersion);
        w.WriteUInt16(4);
        w.WriteByte(2);
        w.WriteByte(7);
        w.WriteByte(0);
        w.WriteByte(0);

        w.WriteUInt16((ushort)(ParameterId.VendorSpecificFlag | ParameterId.ProtocolVersion));
        w.WriteUInt16(4);
        w.WriteByte(9);
        w.WriteByte(9);
        w.WriteByte(0);
        w.WriteByte(0);

        w.WriteUInt16(ParameterId.Sentinel);
        w.WriteUInt16(0);
        int total = w.Position;

        var r = new CdrReader(buf.AsSpan(0, total), CdrEndianness.LittleEndian);
        var data = ParticipantDataSerializer.Read(ref r);
        data.ProtocolVersion.Should().Be(new ProtocolVersion(2, 7));
    }

    [Fact]
    public void 既知_must_understand_PID_は標準_PID_として解釈される()
    {
        var buf = new byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteUInt16((ushort)(ParameterId.MustUnderstandFlag | ParameterId.ProtocolVersion));
        w.WriteUInt16(4);
        w.WriteByte(2);
        w.WriteByte(7);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteUInt16(ParameterId.Sentinel);
        w.WriteUInt16(0);
        int total = w.Position;

        var r = new CdrReader(buf.AsSpan(0, total), CdrEndianness.LittleEndian);
        var data = ParticipantDataSerializer.Read(ref r);
        data.ProtocolVersion.Should().Be(new ProtocolVersion(2, 7));
    }

    [Fact]
    public void PID_DOMAIN_TAG_は既知_must_understand_PID_としてスキップされる()
    {
        var buf = new byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        var pl = new ParameterListWriter(w);

        pl.BeginParameter(ParameterId.DomainTag);
        pl.WriteString("");
        pl.EndParameter();

        pl.BeginParameter(ParameterId.ProtocolVersion);
        pl.WriteByte(2);
        pl.WriteByte(5);
        pl.EndParameter();

        pl.WriteSentinel();
        var serialized = buf[..pl.CurrentWriter.Position].ToArray();

        var r = new CdrReader(serialized, CdrEndianness.LittleEndian);
        var data = ParticipantDataSerializer.Read(ref r);

        data.ProtocolVersion.Should().Be(new ProtocolVersion(2, 5));
    }

    [Fact]
    public void entity_nameは上限ちょうどなら受理し超過なら拒否する()
    {
        var ok = MakeSampleData();
        ok.EntityName = "abc";
        var buf = new byte[1024];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, ok);

        ReadParticipantData(buf, w.Position, new DiscoveryLimits(maxEntityNameBytes: 4))
            .EntityName.Should().Be("abc");

        var tooLong = MakeSampleData();
        tooLong.EntityName = "abcd";
        w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, tooLong);
        int tooLongLength = w.Position;

        Action act = () => ReadParticipantData(buf, tooLongLength, new DiscoveryLimits(maxEntityNameBytes: 4));
        act.Should().Throw<InvalidDataException>().WithMessage("*exceeds limit 4*");
    }

    [Fact]
    public void locator数が上限を超えたら拒否する()
    {
        var data = MakeSampleData();
        data.DefaultUnicastLocators.Add(Locator.FromUdpV4(IPAddress.Parse("192.168.1.11"), 7413u));
        var buf = new byte[1024];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, data);
        int length = w.Position;

        Action act = () => ReadParticipantData(buf, length, new DiscoveryLimits(maxParticipantLocators: 2));
        act.Should().Throw<InvalidDataException>().WithMessage("*locator count*");
    }

    [Fact]
    public void lease_durationは指定範囲にclampされる()
    {
        var data = MakeSampleData();
        data.LeaseDuration = Duration.Infinite;
        var buf = new byte[1024];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, data);

        var limits = new DiscoveryLimits(
            minRemoteParticipantLeaseSeconds: 1,
            maxRemoteParticipantLeaseSeconds: 2);
        ReadParticipantData(buf, w.Position, limits)
            .LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        data.LeaseDuration = Duration.Zero;
        w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        ParticipantDataSerializer.Write(ref w, data);

        ReadParticipantData(buf, w.Position, limits)
            .LeaseDuration.ToTimeSpan()
            .Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void unknown_must_understand_PID_は拒否される()
    {
        var buf = new byte[256];
        var w = new CdrWriter(buf, CdrEndianness.LittleEndian);
        w.WriteUInt16(0x4242);
        w.WriteUInt16(4);
        w.WriteUInt32(0xDEADBEEFu);
        w.WriteUInt16(ParameterId.Sentinel);
        w.WriteUInt16(0);
        int total = w.Position;

        var act = () => ReadParticipantData(buf, total);

        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*0x4242*");
    }
}
