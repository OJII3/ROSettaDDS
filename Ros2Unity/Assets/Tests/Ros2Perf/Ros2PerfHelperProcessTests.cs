using System.IO;
using NUnit.Framework;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class Ros2PerfHelperProcessTests
    {
        [Test]
        public void ResolveExecutable_は_env_を優先する()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, "/tmp/ros2_perf_helper");
                Assert.AreEqual("/tmp/ros2_perf_helper", Ros2PerfHelperProcess.ResolveExecutablePath());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, original);
            }
        }

        [Test]
        public void ResolveExecutable_は_env_が空なら_default_path_を返す()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, string.Empty);

                string expectedSuffix = Path.Combine(
                    "tools",
                    "ros2-perf-helper",
                    "install",
                    "rosettadds_ros2_perf_helper",
                    "lib",
                    "rosettadds_ros2_perf_helper",
                    "ros2_perf_helper");
                StringAssert.EndsWith(expectedSuffix, Ros2PerfHelperProcess.ResolveExecutablePath());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, original);
            }
        }
    }
}
