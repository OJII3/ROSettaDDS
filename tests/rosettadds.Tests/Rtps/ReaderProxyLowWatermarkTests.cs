using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Rtps;

public class ReaderProxyLowWatermarkTests
{
    private static readonly Guid TestReaderGuid = new(
        GuidPrefix.Unknown, EntityId.Unknown);

    [Fact]
    public void 初期状態では_LowWatermark_未設定()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.IsLowWatermarkSet.Should().BeFalse();
        proxy.LowWatermark.Should().BeNull();
    }

    [Fact]
    public void SetLowWatermark_で_設定される()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.SetLowWatermark(new SequenceNumber(42L));

        proxy.IsLowWatermarkSet.Should().BeTrue();
        proxy.LowWatermark.Should().Be(new SequenceNumber(42L));
    }

    [Fact]
    public void SetLowWatermark_は_2回目以降_無視される()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.SetLowWatermark(new SequenceNumber(10L));
        proxy.SetLowWatermark(new SequenceNumber(20L));

        proxy.LowWatermark.Should().Be(new SequenceNumber(10L));
    }

    [Fact]
    public void IsPreJoin_は_未設定時_false()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.IsPreJoin(new SequenceNumber(1L)).Should().BeFalse();
        proxy.IsPreJoin(new SequenceNumber(0L)).Should().BeFalse();
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(5L, true)]
    [InlineData(10L, true)]
    [InlineData(11L, false)]
    [InlineData(100L, false)]
    public void IsPreJoin_は_watermark_以下なら_true(long sn, bool expected)
    {
        var proxy = new ReaderProxy(TestReaderGuid);
        proxy.SetLowWatermark(new SequenceNumber(10L));

        proxy.IsPreJoin(new SequenceNumber(sn)).Should().Be(expected);
    }

    [Fact]
    public void IsPreJoin_は_SN0_に対して_false()
    {
        var proxy = new ReaderProxy(TestReaderGuid);
        proxy.SetLowWatermark(new SequenceNumber(10L));

        proxy.IsPreJoin(new SequenceNumber(0L)).Should().BeFalse();
    }

    [Fact]
    public void Reliable_Reader_Proxy_は_生成できる()
    {
        var proxy = new ReaderProxy(TestReaderGuid, reliability: ReliabilityKind.Reliable);

        proxy.IsReliable.Should().BeTrue();
    }
}
