using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class ProgramTests
{
    [Fact]
    public void BuildHelperEnv_Android_build_target_で_RosLocalhostOnly_が_0()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        var env = new Dictionary<string, string?>();
        Program.BuildHelperEnv(options, domainId: 42, env);

        env.Should().ContainKey("ROS_LOCALHOST_ONLY");
        env["ROS_LOCALHOST_ONLY"].Should().Be("0");
        env.Should().ContainKey("RMW_IMPLEMENTATION").WhoseValue.Should().Be("rmw_fastrtps_cpp");
        env.Should().ContainKey("ROS_DOMAIN_ID").WhoseValue.Should().Be("42");
    }

    [Fact]
    public void BuildHelperEnv_StandaloneLinux64_build_target_で_RosLocalhostOnly_が_1()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        var env = new Dictionary<string, string?>();
        Program.BuildHelperEnv(options, domainId: 1, env);

        env["ROS_LOCALHOST_ONLY"].Should().Be("1");
    }

    [Fact]
    public void CreatePlayerDriver_Android_build_target_で_AndroidAdbDriver_を返す()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android", "--android-device", "FAKE123" });
        using var driver = Program.CreatePlayerDriver(options, artifactDir: "/tmp/x", apkFile: "/tmp/x.apk");
        driver.Should().BeOfType<AndroidAdbDriver>();
    }

    [Fact]
    public void CreatePlayerDriver_StandaloneLinux64_build_target_で_DesktopProcessDriver_を返す()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        using var driver = Program.CreatePlayerDriver(options, artifactDir: "/tmp/x", apkFile: null);
        driver.Should().BeOfType<DesktopProcessDriver>();
    }

    [Fact]
    public void CreateHelperDriver_は_desktop_と_android_両方で_DesktopProcessDriver_を返す()
    {
        var android = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        Program.CreateHelperDriver(android).Should().BeOfType<DesktopProcessDriver>();

        var linux = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        Program.CreateHelperDriver(linux).Should().BeOfType<DesktopProcessDriver>();
    }
}
