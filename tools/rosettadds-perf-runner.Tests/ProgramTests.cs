using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class ProgramMultiRunTests
{
}

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
    public void CreatePlayerDriver_Android_で_android_device_未指定なら_例外()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        var act = () => Program.CreatePlayerDriver(options, "/tmp/x", "/tmp/x.apk");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*--android-device*");
    }

    [Fact]
    public void CreateHelperDriver_は_desktop_と_android_両方で_DesktopProcessDriver_を返す()
    {
        var android = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        Program.CreateHelperDriver(android).Should().BeOfType<DesktopProcessDriver>();

        var linux = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        Program.CreateHelperDriver(linux).Should().BeOfType<DesktopProcessDriver>();
    }

    [Fact]
    public void Android_build_target_の_player_は_AndroidAdbDriver_helper_は_DesktopProcessDriver()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android", "--android-device", "FAKE123" });
        using var player = Program.CreatePlayerDriver(options, "/tmp/x", "/tmp/x.apk");
        using var helper = Program.CreateHelperDriver(options);

        player.Should().BeOfType<AndroidAdbDriver>();
        helper.Should().BeOfType<DesktopProcessDriver>();
    }

    [Fact]
    public void Android_build_target_の_player_artifact_dir_が_PersistentDir_に_反映される()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android", "--android-device", "FAKE123" });
        using var driver = Program.CreatePlayerDriver(options, "/tmp/x", "/tmp/x.apk");
        var android = (AndroidAdbDriver)driver;
        var field = typeof(AndroidAdbDriver).GetField(
            "_devicePersistentDir",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.GetValue(android).Should().Be(
            "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf");
    }
}
