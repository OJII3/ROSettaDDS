using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            if (string.IsNullOrEmpty(fromEnv))
            {
                throw new InvalidOperationException(
                    HelperEnvKey + " is not set. " +
                    "Run the perf test from a shell where the helper path is exported " +
                    "(see docs/unity-ros2-perf-results.md).");
            }
            return fromEnv;
        }

        internal static bool IsAvailable()
        {
            string path = Environment.GetEnvironmentVariable(HelperEnvKey);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
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
