using ROSettaDDS.Common;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class LeaseExpiryMonitorTests
{
    [Fact]
    public void CheckPeriod_は_SpdpInterval_と_LeaseDuration_の短い正値を使い_下限を適用する()
    {
        var options = new DomainParticipantOptions
        {
            SpdpInterval = TimeSpan.FromMilliseconds(20),
            LeaseDuration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
        };

        LeaseExpiryMonitor.ComputeCheckPeriod(options)
            .Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void CheckPeriod_は_最大1秒を超えない()
    {
        var options = new DomainParticipantOptions
        {
            SpdpInterval = TimeSpan.FromSeconds(10),
            LeaseDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(8)),
        };

        LeaseExpiryMonitor.ComputeCheckPeriod(options)
            .Should().Be(TimeSpan.FromSeconds(1));
    }
}
