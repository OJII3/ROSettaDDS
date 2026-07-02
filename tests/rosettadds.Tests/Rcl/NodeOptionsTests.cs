using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class NodeOptionsTests
{
    [Fact]
    public void 既定の_Logger_は_null()
    {
        var opts = new NodeOptions();
        Assert.Null(opts.Logger);
    }

    [Fact]
    public void Default_は新しいインスタンス()
    {
        var a = NodeOptions.Default;
        var b = NodeOptions.Default;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(a.Logger);
        Assert.Null(b.Logger);
    }

    [Fact]
    public void Logger_を_override_できる()
    {
        var custom = new ConsoleLogger("test", LogLevel.Debug);
        var opts = new NodeOptions { Logger = custom };
        Assert.Same(custom, opts.Logger);
    }
}
