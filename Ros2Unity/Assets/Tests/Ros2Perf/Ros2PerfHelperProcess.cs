using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ROSettaDDS.UnityRos2Perf.Tests
{
    internal sealed class Ros2PerfHelperProcess : IDisposable
    {
        private const string HelperEnvKey = "ROSETTADDS_ROS2_PERF_HELPER";
        private readonly Process _process;
        private readonly List<string> _stdout = new List<string>();
        private readonly List<string> _stderr = new List<string>();
        private readonly object _gate = new object();
        private int _nextStdoutLineToParse;

        private Ros2PerfHelperProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stdout.Add(e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderr.Add(e.Data);
                }
            };
        }

        internal static string ResolveExecutablePath()
        {
            string fromEnv = Environment.GetEnvironmentVariable(HelperEnvKey);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }

            string cwd = Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(
                cwd,
                "..",
                "tools",
                "ros2-perf-helper",
                "install",
                "rosettadds_ros2_perf_helper",
                "lib",
                "rosettadds_ros2_perf_helper",
                "ros2_perf_helper"));
        }

        internal static string ResolveRos2Install()
        {
            string fromEnv = Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                foreach (string candidate in fromEnv.Split(System.IO.Path.PathSeparator))
                {
                    if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            foreach (string distro in new[] { "humble", "iron", "jazzy", "foxy" })
            {
                string candidate = "/opt/ros/" + distro;
                if (Directory.Exists(System.IO.Path.Combine(candidate, "lib")))
                {
                    return candidate;
                }
            }

            if (Directory.Exists("/nix/store"))
            {
                foreach (string dir in Directory.EnumerateDirectories("/nix/store", "*-ros-env"))
                {
                    if (Directory.Exists(System.IO.Path.Combine(dir, "lib")))
                    {
                        return dir;
                    }
                }
                foreach (string dir in Directory.EnumerateDirectories("/nix/store", "*-ros-humble*"))
                {
                    if (Directory.Exists(System.IO.Path.Combine(dir, "lib")))
                    {
                        return dir;
                    }
                }
            }

            return null;
        }

        internal static bool IsAvailable()
        {
            return File.Exists(ResolveExecutablePath());
        }

        internal static Ros2PerfHelperProcess Start(string arguments, int domainId, string qos)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveExecutablePath(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            string ros2Install = ResolveRos2Install();
            if (!string.IsNullOrEmpty(ros2Install))
            {
                if (!startInfo.Environment.ContainsKey("AMENT_PREFIX_PATH") ||
                    string.IsNullOrEmpty(startInfo.Environment["AMENT_PREFIX_PATH"]))
                {
                    startInfo.Environment["AMENT_PREFIX_PATH"] = ros2Install;
                }
                string libPath = System.IO.Path.Combine(ros2Install, "lib");
                if (!startInfo.Environment.ContainsKey("LD_LIBRARY_PATH") ||
                    string.IsNullOrEmpty(startInfo.Environment["LD_LIBRARY_PATH"]))
                {
                    startInfo.Environment["LD_LIBRARY_PATH"] = libPath;
                }
                else if (!startInfo.Environment["LD_LIBRARY_PATH"].Split(System.IO.Path.PathSeparator).Contains(libPath))
                {
                    startInfo.Environment["LD_LIBRARY_PATH"] = libPath + System.IO.Path.PathSeparator + startInfo.Environment["LD_LIBRARY_PATH"];
                }
            }
            startInfo.Environment["ROS_LOCALHOST_ONLY"] = "1";
            startInfo.Environment["RMW_IMPLEMENTATION"] = "rmw_fastrtps_cpp";
            startInfo.Environment["ROS_DOMAIN_ID"] = domainId.ToString(CultureInfo.InvariantCulture);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            var wrapper = new Ros2PerfHelperProcess(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return wrapper;
        }

        internal bool TryWaitForEvent(
            Ros2PerfHelperEventKind kind,
            TimeSpan timeout,
            out Ros2PerfHelperEvent found,
            out string error)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                List<string> snapshot;
                int startIndex;
                lock (_gate)
                {
                    snapshot = new List<string>(_stdout);
                    startIndex = _nextStdoutLineToParse;
                }

                for (int i = startIndex; i < snapshot.Count; i++)
                {
                    if (!Ros2PerfHelperEvent.TryParse(snapshot[i], out var parsed, out error))
                    {
                        ConsumeStdoutThrough(i);
                        found = default;
                        return false;
                    }

                    ConsumeStdoutThrough(i);
                    if (parsed.Kind == Ros2PerfHelperEventKind.Error)
                    {
                        found = parsed;
                        error = parsed.Message;
                        return false;
                    }

                    if (parsed.Kind == kind)
                    {
                        found = parsed;
                        error = null;
                        return true;
                    }
                }

                Thread.Sleep(10);
            }

            found = default;
            error = "Timed out waiting for " + kind + ". Output tail:\n" + OutputTail();
            return false;
        }

        internal string OutputTail()
        {
            lock (_gate)
            {
                var builder = new StringBuilder();
                builder.AppendLine("stdout:");
                AppendTail(builder, _stdout);
                builder.AppendLine("stderr:");
                AppendTail(builder, _stderr);
                return builder.ToString();
            }
        }

        internal string StdoutSnapshot()
        {
            lock (_gate)
            {
                return string.Join("\n", _stdout);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        internal void WaitForExit(System.TimeSpan timeout)
        {
            if (_process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                _process.WaitForExit();
                return;
            }
            try
            {
                _process.Kill();
                _process.WaitForExit(2000);
            }
            catch
            {
            }
        }

        private void ConsumeStdoutThrough(int index)
        {
            lock (_gate)
            {
                _nextStdoutLineToParse = Math.Max(_nextStdoutLineToParse, index + 1);
            }
        }

        private static void AppendTail(StringBuilder builder, List<string> lines)
        {
            int start = Math.Max(0, lines.Count - 20);
            for (int i = start; i < lines.Count; i++)
            {
                builder.AppendLine(lines[i]);
            }
        }
    }
}
