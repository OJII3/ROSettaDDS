namespace ROSettaDDS.PerfRunner;

internal enum PerfDirection
{
    UnityToRos2,
    Ros2ToUnity,
}

internal sealed class PerfScenario
{
    private PerfScenario(string name, PerfDirection direction, string qos, int payloadBytes, int messages)
    {
        Name = name;
        Direction = direction;
        Qos = qos;
        PayloadBytes = payloadBytes;
        Messages = messages;
    }

    internal string Name { get; }
    internal PerfDirection Direction { get; }
    internal string Qos { get; }
    internal int PayloadBytes { get; }
    internal int Messages { get; }

    internal string DirectionArgument => Direction == PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity";

    internal static IReadOnlyList<PerfScenario> All { get; } = new[]
    {
        new PerfScenario("unity-to-ros2-reliable-32", PerfDirection.UnityToRos2, "reliable", 32, 500),
        new PerfScenario("unity-to-ros2-reliable-1024", PerfDirection.UnityToRos2, "reliable", 1024, 500),
        new PerfScenario("unity-to-ros2-reliable-1400", PerfDirection.UnityToRos2, "reliable", 1400, 500),
        new PerfScenario("unity-to-ros2-reliable-8000", PerfDirection.UnityToRos2, "reliable", 8000, 200),
        new PerfScenario("unity-to-ros2-best-effort-8192", PerfDirection.UnityToRos2, "best_effort", 8192, 200),
        new PerfScenario("ros2-to-unity-reliable-32", PerfDirection.Ros2ToUnity, "reliable", 32, 500),
        new PerfScenario("ros2-to-unity-reliable-1024", PerfDirection.Ros2ToUnity, "reliable", 1024, 500),
        new PerfScenario("ros2-to-unity-best-effort-8192", PerfDirection.Ros2ToUnity, "best_effort", 8192, 200),
        new PerfScenario("ros2-to-unity-best-effort-32k", PerfDirection.Ros2ToUnity, "best_effort", 32768, 100),
    };

    internal static IReadOnlyList<PerfScenario> Select(string value)
    {
        if (value == "all")
        {
            return All;
        }

        foreach (PerfScenario scenario in All)
        {
            if (scenario.Name == value)
            {
                return new[] { scenario };
            }
        }

        throw new ArgumentException("unknown scenario: " + value);
    }
}
