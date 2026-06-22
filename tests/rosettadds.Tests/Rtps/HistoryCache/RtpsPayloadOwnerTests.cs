using System.Buffers;
using ROSettaDDS.Rtps.HistoryCache;

namespace ROSettaDDS.Tests.Rtps.HistoryCache;

public class RtpsPayloadOwnerTests
{
    [Fact]
    public void Dispose_で_owner_の_buffer_が_ArrayPool_に_戻される()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        owner.Dispose();

        Action act = () => _ = owner.Buffer;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_を_2回呼んでも_安全()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);

        owner.Dispose();
        owner.Dispose();
    }

    [Fact]
    public void Buffer_は_constructor_に_渡した_配列_を_返す()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);

        owner.Buffer.Should().BeSameAs(buffer);

        ArrayPool<byte>.Shared.Return(buffer);
    }

    [Fact]
    public void Buffer_は_Dispose_後に_ObjectDisposedException_を_投げる()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var owner = new RtpsPayloadOwner(buffer);
        owner.Dispose();

        Action act = () => _ = owner.Buffer;
        act.Should().Throw<ObjectDisposedException>();
    }
}
