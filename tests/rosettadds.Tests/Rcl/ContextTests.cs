using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rtps;
using Guid = ROSettaDDS.Common.Guid;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class ContextTests
{
    [Fact]
    public void コンストラクタは_GuidPrefix_を生成する()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotEqual(default, ctx.GuidPrefix);
        Assert.Equal(new Guid(ctx.GuidPrefix, BuiltinEntityIds.Participant), ctx.Guid);
    }

    [Fact]
    public void コンストラクタは_options_を保持する()
    {
        var opts = new ContextOptions { DomainId = 42, LocalhostOnly = true, Logger = NullLogger.Instance };
        using var ctx = new Context(opts);
        Assert.Same(opts, ctx.Options);
    }

    [Fact]
    public void null_options_を渡すと_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Context(null!));
    }

    [Fact]
    public void コンストラクタ完了時に_transport_4_種と_DiscoveryDb_が利用可能な状態になる()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        // Start 前は transport を内部で作っているが、外から見える形にしていないので
        // ResolvedParticipantId / DiscoveryDb が NotImplementedException にならないことだけ確認。
        // auto-probe が ID 0 で成功することがあるため、値自体は 0 以上であれば良い。
        Assert.True(ctx.ResolvedParticipantId >= 0);
        Assert.NotNull(ctx.DiscoveryDb);
    }

    [Fact]
    public void UserMulticast_と_UserUnicast_と_Destination_が_context_から取得できる()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotNull(ctx.UserMulticastTransport);
        Assert.NotNull(ctx.UserUnicastTransport);
        Assert.NotEqual(Locator.Invalid, ctx.UserMulticastDestination);
    }

    [Fact]
    public void Start_Stop_は_冪等()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Start();
        ctx.Start();  // 二度呼んでも例外なし
        ctx.Stop();
        ctx.Stop();   // 二度呼んでも例外なし
    }

    [Fact]
    public void Dispose_後は_Start_が_ObjectDisposedException()
    {
        var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ctx.Start());
    }

    [Fact]
    public void Dispose_後は_Stop_が_ObjectDisposedException()
    {
        var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ctx.Stop());
    }

    [Fact]
    public void Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する()
    {
        var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        var node = new Node(ctx, "test");
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => node.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
    }
}
