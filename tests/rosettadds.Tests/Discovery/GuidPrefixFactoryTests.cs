using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Discovery;

public class GuidPrefixFactoryTests
{
    [Fact]
    public void Create_は_先頭2バイトに_VendorId_を埋め込む()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, hostId: 0x11223344, processId: 0x55667788, instanceCounter: 0x99AA);
        prefix[0].Should().Be(VendorId.ROSettaDDS.V0);
        prefix[1].Should().Be(VendorId.ROSettaDDS.V1);
        prefix[2].Should().Be((byte)0x11);
        prefix[3].Should().Be((byte)0x22);
        prefix[4].Should().Be((byte)0x33);
        prefix[5].Should().Be((byte)0x44);
        prefix[6].Should().Be((byte)0x55);
        prefix[7].Should().Be((byte)0x66);
        prefix[8].Should().Be((byte)0x77);
        prefix[9].Should().Be((byte)0x88);
        prefix[10].Should().Be((byte)0x99);
        prefix[11].Should().Be((byte)0xAA);
    }

    [Fact]
    public void CreateForCurrentProcess_は_異なる_counter_でユニーク()
    {
        var a = GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS);
        var b = GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS);
        // counter (last 2 bytes) で必ず差が出る
        a.Should().NotBe(b);
        a[0].Should().Be(VendorId.ROSettaDDS.V0);
        a[1].Should().Be(VendorId.ROSettaDDS.V1);
    }
}
