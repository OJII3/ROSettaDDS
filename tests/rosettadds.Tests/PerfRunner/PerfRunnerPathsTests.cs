using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.Tests.PerfRunner;

public class PerfRunnerPathsTests
{
    [Fact]
    public void ResolvePlayerBuildPathUsesExistingBuildWhenSkippingBuild()
    {
        var options = RunnerOptions.Parse(new[]
        {
            "--skip-build",
            "--player-build",
            "/tmp/existing-player",
        });

        string path = PerfRunnerPaths.ResolvePlayerBuildPath("/tmp/run", options);

        path.Should().Be("/tmp/existing-player");
    }

    [Fact]
    public void ResolvePlayerBuildPathUsesRunDirectoryWhenBuilding()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());

        string path = PerfRunnerPaths.ResolvePlayerBuildPath("/tmp/run", options);

        path.Should().Be(Path.Combine("/tmp/run", "build", "ROSettaDDSPerfPlayer"));
    }
}
