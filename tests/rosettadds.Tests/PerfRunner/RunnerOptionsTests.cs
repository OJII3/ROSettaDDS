using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.Tests.PerfRunner;

public class RunnerOptionsTests
{
    [Fact]
    public void ParseRequiresPlayerBuildWhenSkippingBuild()
    {
        Action act = () => RunnerOptions.Parse(new[] { "--skip-build" });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*--player-build*");
    }

    [Fact]
    public void ParseAcceptsExplicitPlayerBuildWhenSkippingBuild()
    {
        RunnerOptions options = RunnerOptions.Parse(new[]
        {
            "--skip-build",
            "--player-build",
            "/tmp/ROSettaDDSPerfPlayer",
        });

        options.SkipBuild.Should().BeTrue();
        options.PlayerBuild.Should().Be("/tmp/ROSettaDDSPerfPlayer");
    }
}
