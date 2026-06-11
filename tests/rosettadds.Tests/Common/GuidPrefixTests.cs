using ROSettaDDS.Common;

namespace ROSettaDDS.Tests.Common;

public class GuidPrefixTests
{
    [Fact]
    public void Size_は_12()
    {
        GuidPrefix.Size.Should().Be(12);
    }

    [Fact]
    public void コンストラクタは_12バイト以外を拒否する()
    {
        Action act11 = () => _ = new GuidPrefix(new byte[11]);
        Action act13 = () => _ = new GuidPrefix(new byte[13]);
        act11.Should().Throw<ArgumentException>();
        act13.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 構築した内容が_CopyTo_で復元できる()
    {
        var src = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var prefix = new GuidPrefix(src);

        var dest = new byte[12];
        prefix.CopyTo(dest);
        dest.Should().Equal(src);
    }

    [Fact]
    public void Indexer_で各バイトに参照できる()
    {
        var src = new byte[12];
        for (int i = 0; i < 12; i++) src[i] = (byte)(i * 17);
        var prefix = new GuidPrefix(src);

        for (int i = 0; i < 12; i++)
        {
            prefix[i].Should().Be((byte)(i * 17));
        }
    }

    [Fact]
    public void Unknown_は_全0()
    {
        var bytes = GuidPrefix.Unknown.ToByteArray();
        bytes.Should().HaveCount(12);
        bytes.Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void 同値の_GuidPrefix_は_等価()
    {
        var a = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var b = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        var c = new GuidPrefix(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 99 });

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_は_16進大文字_24文字()
    {
        var prefix = new GuidPrefix(new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAB, 0xCD, 0xEF, 0x10, 0xFF, 0x00, 0x12, 0x34 });
        prefix.ToString().Should().Be("01020304ABCDEF10FF001234");
    }
}
