using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class AndroidAdbDriverTests : IDisposable
{
    private readonly string _tmp;
    private readonly FakeAdbClient _fake;
    private readonly AdbClient _client;
    private readonly AndroidAdbDriver _driver;

    public AndroidAdbDriverTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "rosettadds-android-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _fake = new FakeAdbClient();
        _client = new AdbClient(_fake, serial: "ABC");
        _driver = new AndroidAdbDriver(
            adb: _client,
            packageId: "com.ojii3.rosettadds.perf",
            activityComponent: "com.unity3d.player.GameActivity",
            devicePersistentDir: "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf",
            localArtifactDir: _tmp);
    }

    public void Dispose()
    {
        _driver.Dispose();
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static LaunchSpec Spec() => new(
        Kind: "player",
        ScenarioName: "x",
        Direction: "unity_to_ros2",
        DomainId: 42,
        Topic: "/t",
        Qos: "reliable",
        PayloadBytes: 32,
        Messages: 100,
        LocalhostOnly: false,
        ReadyFile: "ready",
        DoneFile: "done",
        ReleaseFile: null,
        MetricsFile: "metrics.ndjson",
        PlayerExecutable: "/nonexistent",
        ApkFile: "/tmp/x.apk",
        DevicePersistentDir: "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf",
        HelperMeasureStart: 0,
        HelperMode: "sub",
        HelperTopic: "/t",
        ExtraArgs: new[] { "--rosettadds-topic", "/t", "--rosettadds-localhost-only", "false" });

    [Fact]
    public async Task StartAsync_は_install_force_stop_am_start_の_順に_呼ぶ()
    {
        await _driver.StartAsync(Spec(), CancellationToken.None);

        _fake.Calls.Should().HaveCount(3);
        _fake.Calls[0].Should().StartWith("adb -s ABC install -r /tmp/x.apk");
        _fake.Calls[1].Should().Be("adb -s ABC shell am force-stop com.ojii3.rosettadds.perf");
        _fake.Calls[2].Should().StartWith("adb -s ABC shell 'am start -W -n com.ojii3.rosettadds.perf/com.unity3d.player.GameActivity");
        _fake.Calls[2].Should().Contain("--es args");
    }

    [Fact]
    public async Task StartAsync_install_失敗で_例外()
    {
        _fake.ExitCodeOverride = 1;
        _fake.StderrOverride = "Failure [INSTALL_FAILED]";
        var act = () => _driver.StartAsync(Spec(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*install*Failure*");
    }

    [Fact]
    public async Task WaitForSentinelAsync_1回目失敗_2回目成功で_true()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 1, 0 });
        _fake.FileProvider = (remote, local) =>
        {
            File.WriteAllText(local, "ok");
        };
        bool ok = await _driver.WaitForSentinelAsync("ready", TimeSpan.FromSeconds(2), CancellationToken.None);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForSentinelAsync_全失敗で_false()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });
        bool ok = await _driver.WaitForSentinelAsync("ready", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForSentinelAsync_pull_以外_の_exit_code_で_IOException()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 137 });
        var act = () => _driver.WaitForSentinelAsync("ready", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task WaitForExitAsync_pidof_が_空_を返したら_0_を返す()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 1 });
        int code = await _driver.WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        code.Should().Be(0);
    }

    [Fact]
    public async Task WaitForExitAsync_pidof_が_空_になる_まで_polling_継続()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 0, 0, 1 });
        int code = await _driver.WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        code.Should().Be(0);
        _fake.Calls.Count(c => c.Contains("pidof")).Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task WaitForExitAsync_タイムアウト時_Kill_して_TimeoutException()
    {
        _fake.ScriptedExitCodes = new Queue<int>(Enumerable.Repeat(0, 100));
        var act = () => _driver.WaitForExitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>();
        _fake.Calls.Should().Contain(c => c.Contains("am force-stop"));
    }

    [Fact]
    public async Task StartActivity_は_outer_single_quote_で_device_sh_に_渡す()
    {
        _fake.ExitCodeOverride = 1;
        var act = () => _client.StartActivityAsync(
            "com.example.nonexistent",
            "com.example.MainActivity",
            new[] { "--rosettadds-perf", "--rosettadds-topic", "/t" },
            CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _fake.Calls.Should().ContainSingle()
            .Which.Should().Be(
                "adb -s ABC shell 'am start -W -n com.example.nonexistent/com.example.MainActivity " +
                "--es args \"--rosettadds-perf --rosettadds-topic /t\"'");
    }

    [Fact]
    public async Task CleanStaleSentinelsAsync_は_各_sentinel_に対して_adb_shell_rm_f_を呼ぶ()
    {
        await _driver.CleanStaleSentinelsAsync(
            new[] { "ready", "done", "metrics.ndjson" }, CancellationToken.None);

        _fake.Calls.Should().HaveCount(3);
        _fake.Calls[0].Should().Be("adb -s ABC shell rm -f /sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf/ready");
        _fake.Calls[1].Should().Be("adb -s ABC shell rm -f /sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf/done");
        _fake.Calls[2].Should().Be("adb -s ABC shell rm -f /sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf/metrics.ndjson");
    }
}
