using System;
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void 仮テスト()
    {
        true.Should().BeTrue();
    }

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
}
