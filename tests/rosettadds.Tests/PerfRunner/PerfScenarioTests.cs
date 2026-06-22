using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.Tests.PerfRunner;

public class PerfScenarioTests
{
    [Theory]
    [InlineData("unity-to-ros2-reliable-32", "unity_to_ros2", "reliable", 32, 500)]
    [InlineData("unity-to-ros2-reliable-1024", "unity_to_ros2", "reliable", 1024, 500)]
    [InlineData("unity-to-ros2-reliable-1400", "unity_to_ros2", "reliable", 1400, 500)]
    [InlineData("unity-to-ros2-reliable-8000", "unity_to_ros2", "reliable", 8000, 200)]
    [InlineData("unity-to-ros2-best-effort-8192", "unity_to_ros2", "best_effort", 8192, 200)]
    [InlineData("ros2-to-unity-reliable-32", "ros2_to_unity", "reliable", 32, 500)]
    [InlineData("ros2-to-unity-reliable-1024", "ros2_to_unity", "reliable", 1024, 500)]
    [InlineData("ros2-to-unity-best-effort-8192", "ros2_to_unity", "best_effort", 8192, 200)]
    [InlineData("ros2-to-unity-best-effort-32k", "ros2_to_unity", "best_effort", 32768, 100)]
    public void SelectReturnsRegisteredScenario(
        string name, string direction, string qos, int payloadBytes, int messages)
    {
        PerfScenario scenario = PerfScenario.Select(name).Single();

        scenario.Name.Should().Be(name);
        scenario.DirectionArgument.Should().Be(direction);
        scenario.Qos.Should().Be(qos);
        scenario.PayloadBytes.Should().Be(payloadBytes);
        scenario.Messages.Should().Be(messages);
    }

    [Fact]
    public void SelectAllReturnsAllRegisteredScenarios()
    {
        IReadOnlyList<PerfScenario> scenarios = PerfScenario.Select("all");

        scenarios.Should().HaveCount(9);
    }

    [Fact]
    public void SelectUnknownNameThrows()
    {
        Action act = () => PerfScenario.Select("not-a-scenario");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not-a-scenario*");
    }
}
