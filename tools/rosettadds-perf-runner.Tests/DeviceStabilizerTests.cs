using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class DeviceStabilizerTests
{
    [Fact]
    public async Task Stabilize_は_wakelock_wifi_recycle_ping_を順に呼ぶ()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(
            client, hostForPing: "192.168.0.20");

        await stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        fake.Calls.Should().HaveCount(4);
        fake.Calls[0].Should().Be("adb -s DEV shell svc power stayon true");
        fake.Calls[1].Should().Be("adb -s DEV shell svc wifi disable");
        fake.Calls[2].Should().Be("adb -s DEV shell svc wifi enable");
        fake.Calls[3].Should().StartWith("adb -s DEV shell ping -c 5 -W 2 192.168.0.20");
    }

    [Fact]
    public async Task Stabilize_は_ping_成功で完了する()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(client, "192.168.0.20");

        Func<Task> act = () => stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stabilize_は_ping_失敗時にタイムアウト例外を投げる()
    {
        var fake = new FakeAdbClient
        {
            ScriptedExitCodes = new Queue<int>(new[] { 0, 0, 0, 1 }),
            ExitCodeOverride = 1,
            StderrOverride = "ping: network unreachable",
        };
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new AndroidDeviceStabilizer(client, "192.168.0.20");

        Func<Task> act = () => stabilizer.StabilizeAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*ping*");
    }

    [Fact]
    public async Task DesktopStabilizer_は_no_op()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "DEV");
        var stabilizer = new DesktopDeviceStabilizer();

        await stabilizer.StabilizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        fake.Calls.Should().BeEmpty();
    }
}
