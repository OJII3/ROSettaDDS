using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rcl;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Dds;

public class ParticipantTransportSetTests
{
    [Fact]
    public void Custom_transport_は借用し_start_stopするが_disposeしない()
    {
        var calls = new List<string>();
        var metatrafficMulticast = new RecordingTransport("meta-mc", 7400, calls);
        var metatrafficUnicast = new RecordingTransport("meta-uc", 7410, calls);
        var userMulticast = new RecordingTransport("user-mc", 7401, calls);
        var userUnicast = new RecordingTransport("user-uc", 7411, calls);

        using (var transports = ParticipantTransportSet.Create(new ContextOptions
        {
            CustomMulticastTransport = metatrafficMulticast,
            CustomUnicastTransport = metatrafficUnicast,
            CustomUserMulticastTransport = userMulticast,
            CustomUserUnicastTransport = userUnicast,
        }))
        {
            transports.Start();
            transports.Stop();
        }

        calls.Should().Equal(
            "meta-mc:start",
            "meta-uc:start",
            "user-mc:start",
            "user-uc:start",
            "user-uc:stop",
            "user-mc:stop",
            "meta-mc:stop",
            "meta-uc:stop");
    }

    [Fact]
    public void Start_と_Stop_は冪等()
    {
        var calls = new List<string>();
        var options = CreateOptions(calls);
        using var transports = ParticipantTransportSet.Create(options);

        transports.Start();
        transports.Start();
        transports.Stop();
        transports.Stop();

        calls.Count(call => call.EndsWith(":start", StringComparison.Ordinal)).Should().Be(4);
        calls.Count(call => call.EndsWith(":stop", StringComparison.Ordinal)).Should().Be(4);
    }

    [Fact]
    public void 既定では全NICを列挙してloopbackを含むunicast_locatorを広告する()
    {
        using var transports = ParticipantTransportSet.Create(new ContextOptions
        {
            DomainId = 71,
        });

        var expected = LocalNetwork.EnumerateUnicastIPv4();
        transports.DefaultUnicastLocators.Should().HaveCount(expected.Count);
        transports.MetatrafficUnicastLocators.Should().HaveCount(expected.Count);

        transports.DefaultUnicastLocators
            .Select(l => l.ToIPAddress())
            .Should().Contain(IPAddress.Loopback);
        // ANY バインドのため広告アドレスに 0.0.0.0 は含めない。
        transports.DefaultUnicastLocators
            .Select(l => l.ToIPAddress())
            .Should().NotContain(IPAddress.Any);
    }

    [Fact]
    public void LocalUnicastAddress指定時はその単一アドレスのみ広告する()
    {
        var addr = IPAddress.Parse("127.0.0.5");
        using var transports = ParticipantTransportSet.Create(new ContextOptions
        {
            DomainId = 72,
            LocalUnicastAddress = addr,
        });

        transports.DefaultUnicastLocators.Should().ContainSingle()
            .Which.ToIPAddress().Should().Be(addr);
        transports.MetatrafficUnicastLocators.Should().ContainSingle()
            .Which.ToIPAddress().Should().Be(addr);
    }

    [Fact]
    public void LocalhostOnly指定時はloopbackのみ広告する()
    {
        using var transports = ParticipantTransportSet.Create(new ContextOptions
        {
            DomainId = 73,
            LocalhostOnly = true,
        });

        transports.DefaultUnicastLocators.Should().ContainSingle()
            .Which.ToIPAddress().Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public void Custom_unicast_transportのlocatorをそのまま広告する()
    {
        var calls = new List<string>();
        using var transports = ParticipantTransportSet.Create(new ContextOptions
        {
            CustomMulticastTransport = new RecordingTransport("meta-mc", 7400, calls),
            CustomUnicastTransport = new RecordingTransport("meta-uc", 7410, calls),
            CustomUserMulticastTransport = new RecordingTransport("user-mc", 7401, calls),
            CustomUserUnicastTransport = new RecordingTransport("user-uc", 7411, calls),
        });

        transports.MetatrafficUnicastLocators.Should().ContainSingle()
            .Which.Port.Should().Be(7410);
        transports.DefaultUnicastLocators.Should().ContainSingle()
            .Which.Port.Should().Be(7411);
    }

    [Fact]
    public void Restartは_owned_transportの参照を維持して広告addressを再列挙する()
    {
        IReadOnlyList<IPAddress> addresses = new[] { IPAddress.Parse("127.0.0.2") };
        using var transports = ParticipantTransportSet.Create(
            new ContextOptions { DomainId = 89 },
            () => addresses);
        var metatrafficMulticast = transports.MetatrafficMulticast;
        var metatrafficUnicast = transports.MetatrafficUnicast;
        var userMulticast = transports.UserMulticast;
        var userUnicast = transports.UserUnicast;
        transports.Start();

        addresses = new[] { IPAddress.Parse("127.0.0.3") };
        transports.RestartOwnedTransports();

        transports.MetatrafficMulticast.Should().BeSameAs(metatrafficMulticast);
        transports.MetatrafficUnicast.Should().BeSameAs(metatrafficUnicast);
        transports.UserMulticast.Should().BeSameAs(userMulticast);
        transports.UserUnicast.Should().BeSameAs(userUnicast);
        transports.MetatrafficUnicastLocators.Select(static locator => locator.ToIPAddress())
            .Should().Equal(IPAddress.Parse("127.0.0.3"));
        transports.DefaultUnicastLocators.Select(static locator => locator.ToIPAddress())
            .Should().Equal(IPAddress.Parse("127.0.0.3"));
    }

    [Fact]
    public void Restartは_custom_transportのライフサイクルを変更しない()
    {
        var calls = new List<string>();
        using var transports = ParticipantTransportSet.Create(CreateOptions(calls));
        transports.Start();
        var beforeRestart = calls.ToArray();

        transports.RestartOwnedTransports();

        calls.Should().Equal(beforeRestart);
    }

    private static ContextOptions CreateOptions(List<string> calls)
        => new()
        {
            CustomMulticastTransport = new RecordingTransport("meta-mc", 7400, calls),
            CustomUnicastTransport = new RecordingTransport("meta-uc", 7410, calls),
            CustomUserMulticastTransport = new RecordingTransport("user-mc", 7401, calls),
            CustomUserUnicastTransport = new RecordingTransport("user-uc", 7411, calls),
        };

    private sealed class RecordingTransport : IRtpsTransport
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public RecordingTransport(string name, uint port, List<string> calls)
        {
            _name = name;
            _calls = calls;
            LocalLocator = Locator.FromUdpV4(IPAddress.Loopback, port);
        }

        public Locator LocalLocator { get; }
        public event Action<ReadOnlyMemory<byte>, Locator>? Received
        {
            add { }
            remove { }
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> packet,
            Locator destination,
            CancellationToken cancellationToken = default)
            => default;

        public void Start() => _calls.Add($"{_name}:start");
        public void Stop() => _calls.Add($"{_name}:stop");
        public void Dispose() => _calls.Add($"{_name}:dispose");
    }
}
