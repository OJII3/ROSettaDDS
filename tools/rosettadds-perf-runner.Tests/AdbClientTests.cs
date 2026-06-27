using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class AdbClientTests
{
    [Fact]
    public async Task InstallApk_は_adb_install_r_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.InstallApkAsync("/tmp/x.apk", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().StartWith("adb -s ABC install -r /tmp/x.apk");
    }

    [Fact]
    public async Task ForceStop_は_adb_shell_am_force_stop_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.ForceStopAsync("com.ojii3.rosettadds.perf", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().Be("adb -s ABC shell am force-stop com.ojii3.rosettadds.perf");
    }

    [Fact]
    public async Task StartActivity_は_全引数を_args_extra_に_連結する()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.StartActivityAsync(
            "com.ojii3.rosettadds.perf",
            "com.unity3d.player.GameActivity",
            new[] { "--rosettadds-topic", "/t", "--rosettadds-domain-id", "42" },
            CancellationToken.None);

        fake.Calls.Should().ContainSingle();
        string call = fake.Calls[0];
        call.Should().StartWith("adb -s ABC shell 'am start -W -n com.ojii3.rosettadds.perf/com.unity3d.player.GameActivity");
        call.Should().Contain("--es args \"--rosettadds-topic /t --rosettadds-domain-id 42\"");
    }

    [Fact]
    public async Task PullFile_は_adb_pull_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.PullFileAsync("/sdcard/x", "/tmp/x", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().Be("adb -s ABC pull /sdcard/x /tmp/x");
    }
}
