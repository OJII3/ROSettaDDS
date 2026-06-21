using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.Tests.PerfRunner;

public class PerfRunnerProcessArgsTests
{
    [Fact]
    public void HelperReadyTimeoutDoesNotIncludePlayerStartup()
    {
        PerfScenario scenario = PerfScenario.Select("unity-to-ros2-reliable-1024").Single();

        List<string> args = PerfRunnerProcessArgs.Helper(
            scenario,
            "/topic",
            "sub",
            measureStart: false).ToList();

        int index = args.IndexOf("--ready-timeout-ms");
        index.Should().BeGreaterThanOrEqualTo(0);
        args[index + 1].Should().Be("15000");
    }
}
