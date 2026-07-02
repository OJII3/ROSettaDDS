using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class ContextOptionsTests
{
    [Fact]
    public void 既定値は_DomainParticipantOptions_と一致する()
    {
        var opts = new ContextOptions();

        Assert.Equal(0, opts.DomainId);
        Assert.Equal(0, opts.ParticipantId);
        Assert.True(opts.AutoProbeParticipantId);
        Assert.Equal(TimeSpan.FromSeconds(3), opts.SpdpInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), opts.SedpInterval);
        Assert.Equal(Duration.FromSeconds(20), opts.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), opts.UserWriterHeartbeatPeriod);
        Assert.Equal(1000, opts.UserWriterHistoryDepth);
        Assert.Null(opts.MulticastInterface);
        Assert.Equal(RtpsConstants.DefaultMulticastAddress, opts.MulticastGroup);
        Assert.Null(opts.LocalUnicastAddress);
        Assert.False(opts.LocalhostOnly);
        Assert.Equal("rosettadds_context", opts.EntityName);
        Assert.Equal(VendorId.ROSettaDDS, opts.VendorId);
        Assert.Equal(ProtocolVersion.V2_4, opts.ProtocolVersion);
        Assert.Same(NullLogger.Instance, opts.Logger);
        Assert.Null(opts.CustomMulticastTransport);
        Assert.Null(opts.CustomUnicastTransport);
        Assert.Null(opts.CustomUserMulticastTransport);
        Assert.Null(opts.CustomUserUnicastTransport);
    }
}
