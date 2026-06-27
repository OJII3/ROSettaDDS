using System;
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void BuildTarget_既定値は_OS_依存()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        var expected = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX)
            ? "StandaloneOSX" : "StandaloneLinux64";
        options.BuildTarget.Should().Be(expected);
    }

    [Fact]
    public void BuildTarget_Android_を_受理する()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        options.BuildTarget.Should().Be("Android");
    }

    [Fact]
    public void BuildTarget_未知の値は_例外()
    {
        var act = () => RunnerOptions.Parse(new[] { "--build-target", "Bogus" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*--build-target*StandaloneLinux64*StandaloneOSX*Android*");
    }

    [Fact]
    public void Adb_既定値は_adb()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.Adb.Should().Be("adb");
    }

    [Fact]
    public void Adb_カスタムパス_が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--adb", "/opt/adb/bin/adb" });
        options.Adb.Should().Be("/opt/adb/bin/adb");
    }

    [Fact]
    public void AndroidDevice_省略時_null()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidDevice.Should().BeNull();
    }

    [Fact]
    public void AndroidDevice_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-device", "ABCDEFG" });
        options.AndroidDevice.Should().Be("ABCDEFG");
    }

    [Fact]
    public void AndroidPackage_既定値は_com_ojii3_rosettadds_perf()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidPackage.Should().Be("com.ojii3.rosettadds.perf");
    }

    [Fact]
    public void AndroidPackage_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-package", "com.example.foo" });
        options.AndroidPackage.Should().Be("com.example.foo");
    }

    [Fact]
    public void AndroidActivity_既定値は_Unity6_の_GameActivity()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidActivity.Should().Be("com.unity3d.player.UnityPlayerGameActivity");
    }

    [Fact]
    public void AndroidActivity_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-activity", "com.example.MainActivity" });
        options.AndroidActivity.Should().Be("com.example.MainActivity");
    }
}
