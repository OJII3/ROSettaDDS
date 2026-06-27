using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class PerfRunnerPathsTests
{
    [Fact]
    public void ResolvePlayerBuildPath_Android_は_apk_拡張子を付ける()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        string runDir = Path.Combine(Path.GetTempPath(), "rosettadds-perf-paths-test-android");
        try
        {
            string actual = PerfRunnerPaths.ResolvePlayerBuildPath(runDir, options);
            actual.Should().EndWith("ROSettaDDSPerfPlayer.apk");
        }
        finally
        {
            if (Directory.Exists(runDir))
            {
                Directory.Delete(runDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvePlayerBuildPath_StandaloneLinux64_は_拡張子なし()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        string runDir = Path.Combine(Path.GetTempPath(), "rosettadds-perf-paths-test-linux");
        try
        {
            string actual = PerfRunnerPaths.ResolvePlayerBuildPath(runDir, options);
            actual.Should().EndWith("ROSettaDDSPerfPlayer");
            actual.Should().NotEndWith(".apk");
        }
        finally
        {
            if (Directory.Exists(runDir))
            {
                Directory.Delete(runDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvePlayerBuildPath_StandaloneOSX_は_app_拡張子を付ける()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneOSX" });
        string runDir = Path.Combine(Path.GetTempPath(), "rosettadds-perf-paths-test-osx");
        try
        {
            string actual = PerfRunnerPaths.ResolvePlayerBuildPath(runDir, options);
            actual.Should().EndWith("ROSettaDDSPerfPlayer.app");
        }
        finally
        {
            if (Directory.Exists(runDir))
            {
                Directory.Delete(runDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvePlayerBuildPath_SkipBuild_は_player_build_を_そのまま返す()
    {
        var options = RunnerOptions.Parse(new[]
        {
            "--build-target", "Android",
            "--skip-build",
            "--player-build", "/tmp/existing.apk",
        });
        string actual = PerfRunnerPaths.ResolvePlayerBuildPath("/tmp", options);
        actual.Should().Be(Path.GetFullPath("/tmp/existing.apk"));
    }
}
