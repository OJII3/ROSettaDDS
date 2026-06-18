namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal enum Ros2PerfDirection
    {
        UnityToRos2,
        Ros2ToUnity,
    }

    internal enum Ros2PerfQos
    {
        Reliable,
        BestEffort,
    }

    internal readonly struct Ros2PerfScenario
    {
        internal Ros2PerfScenario(Ros2PerfDirection direction, Ros2PerfQos qos, int payloadBytes, int fanout, int messageCount)
        {
            Direction = direction;
            Qos = qos;
            PayloadBytes = payloadBytes;
            Fanout = fanout;
            MessageCount = messageCount;
        }

        internal Ros2PerfDirection Direction { get; }
        internal Ros2PerfQos Qos { get; }
        internal int PayloadBytes { get; }
        internal int Fanout { get; }
        internal int MessageCount { get; }

        internal string QosArgument => Qos == Ros2PerfQos.Reliable ? "reliable" : "best_effort";
        internal string DirectionName => Direction == Ros2PerfDirection.UnityToRos2 ? "unity_to_ros2" : "ros2_to_unity";
        internal string FanoutName => Direction == Ros2PerfDirection.UnityToRos2 ? "subscribers_" + Fanout : "publishers_" + Fanout;
        internal string GroupPrefix => "rosettadds.ros2perf." + DirectionName + "." + QosArgument + "." + PayloadBytes + "B." + FanoutName + ".";
    }
}
