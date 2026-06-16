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

        [Test]
        public void TryWaitForEvent_は既読eventを再利用しない()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            const string shellPath = "/bin/sh";
            if (!File.Exists(shellPath))
            {
                Assert.Ignore(shellPath + " is required for this process wrapper test.");
            }

            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, shellPath);
                using (var helper = Ros2PerfHelperProcess.Start(
                    "-c \"printf '%s\\n' '{\\\"event\\\":\\\"progress\\\",\\\"sent\\\":1}' '{\\\"event\\\":\\\"progress\\\",\\\"sent\\\":2}'\"",
                    42,
                    "reliable"))
                {
                    Assert.IsTrue(helper.TryWaitForEvent(
                        Ros2PerfHelperEventKind.Progress,
                        System.TimeSpan.FromSeconds(5),
                        out var first,
                        out var firstError), firstError);
                    Assert.AreEqual(1, first.Sent);

                    Assert.IsTrue(helper.TryWaitForEvent(
                        Ros2PerfHelperEventKind.Progress,
                        System.TimeSpan.FromSeconds(5),
                        out var second,
                        out var secondError), secondError);
                    Assert.AreEqual(2, second.Sent);
                }
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, original);
            }
        }
    }
}
