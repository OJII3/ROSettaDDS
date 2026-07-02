using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;
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
}
