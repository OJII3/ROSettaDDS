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

        [Test]
        public void Start_は親_process_の_LD_LIBRARY_PATH_を継承する()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            const string shellPath = "/bin/sh";
            if (!File.Exists(shellPath))
            {
                Assert.Ignore(shellPath + " is required for this process wrapper test.");
            }

            string originalHelper = System.Environment.GetEnvironmentVariable(key);
            string originalLdPath = System.Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            const string sentinel = "/tmp/opencode/test_ld_path_" + "abcdef0123456789";
            try
            {
                System.Environment.SetEnvironmentVariable(key, shellPath);
                System.Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", sentinel);
                using (var helper = Ros2PerfHelperProcess.Start(
                    "-c \"printf 'LD=%s' \\\"$LD_LIBRARY_PATH\\\"\"",
                    42,
                    "reliable"))
                {
                    helper.WaitForExit(System.TimeSpan.FromSeconds(5));
                    StringAssert.Contains(sentinel, helper.StdoutSnapshot());
                }
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, originalHelper);
                System.Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", originalLdPath);
            }
        }

        [Test]
        public void Start_は親_process_の_AMENT_PREFIX_PATH_を継承する()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            const string shellPath = "/bin/sh";
            if (!File.Exists(shellPath))
            {
                Assert.Ignore(shellPath + " is required for this process wrapper test.");
            }

            string originalHelper = System.Environment.GetEnvironmentVariable(key);
            string originalAment = System.Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            const string sentinel = "/tmp/opencode/test_ament_" + "abcdef0123456789";
            try
            {
                System.Environment.SetEnvironmentVariable(key, shellPath);
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", sentinel);
                using (var helper = Ros2PerfHelperProcess.Start(
                    "-c \"printf 'AMENT=%s' \\\"$AMENT_PREFIX_PATH\\\"\"",
                    42,
                    "reliable"))
                {
                    helper.WaitForExit(System.TimeSpan.FromSeconds(5));
                    StringAssert.Contains(sentinel, helper.StdoutSnapshot());
                }
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, originalHelper);
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", originalAment);
            }
        }

        [Test]
        public void ResolveRos2Install_は_colcon_install_の親に_AMENT_PREFIX_PATH_を見つけた場合それを返す()
        {
            string originalAment = System.Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            try
            {
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", "/tmp");
                string resolved = Ros2PerfHelperProcess.ResolveRos2Install();
                Assert.AreEqual("/tmp", resolved);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", originalAment);
            }
        }

        [Test]
        public void ResolveRos2Install_は_Nix_store_の_ros_env_を発見できる()
        {
            string originalAment = System.Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            try
            {
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", null);
                string resolved = Ros2PerfHelperProcess.ResolveRos2Install();
                if (Directory.Exists("/nix/store"))
                {
                    Assert.IsNotNull(resolved, "expected to find a ros-env in /nix/store");
                    StringAssert.StartsWith("/nix/store/", resolved);
                    Directory.Exists(System.IO.Path.Combine(resolved, "lib"));
                }
                else
                {
                    Assert.Ignore("not on a system with /nix/store");
                }
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", originalAment);
            }
        }

        [Test]
        public void Start_は_AMENT_PREFIX_PATH_未設定_でも_Nix_ros_env_を_発見して_子に引き継ぐ()
        {
            const string key = "ROSETTADDS_ROS2_PERF_HELPER";
            const string shellPath = "/bin/sh";
            if (!File.Exists(shellPath))
            {
                Assert.Ignore(shellPath + " is required for this process wrapper test.");
            }

            string originalHelper = System.Environment.GetEnvironmentVariable(key);
            string originalAment = System.Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            try
            {
                System.Environment.SetEnvironmentVariable(key, shellPath);
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", null);
                if (Ros2PerfHelperProcess.ResolveRos2Install() == null)
                {
                    Assert.Ignore("no ros2 install found on this system");
                }
                using (var helper = Ros2PerfHelperProcess.Start(
                    "-c \"printf 'AMENT=%s' \\\"$AMENT_PREFIX_PATH\\\"\"",
                    42,
                    "reliable"))
                {
                    helper.WaitForExit(System.TimeSpan.FromSeconds(5));
                    string snapshot = helper.StdoutSnapshot();
                    StringAssert.StartsWith("AMENT=/nix/store/", snapshot);
                }
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(key, originalHelper);
                System.Environment.SetEnvironmentVariable("AMENT_PREFIX_PATH", originalAment);
            }
        }
    }
}
