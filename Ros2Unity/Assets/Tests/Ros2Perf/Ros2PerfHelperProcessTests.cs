using System.IO;
using NUnit.Framework;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    public sealed class Ros2PerfHelperProcessTests
    {
        [Test]
        public void ResolveExecutable_は_env_未設定で例外を投げる()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, string.Empty);
                Assert.Throws<System.InvalidOperationException>(() => Ros2PerfHelperProcess.ResolveExecutablePath());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, original);
            }
        }

        [Test]
        public void ResolveExecutable_は_env_の値をそのまま返す()
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
        public void IsAvailable_は_env_が指すファイルが無ければ_false()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            string original = System.Environment.GetEnvironmentVariable(key);
            try
            {
                System.Environment.SetEnvironmentVariable(key, "/nonexistent/path/ros2_perf_helper");
                Assert.IsFalse(Ros2PerfHelperProcess.IsAvailable());
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
