using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
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

        using (var transports = ParticipantTransportSet.Create(new DomainParticipantOptions
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

    private static DomainParticipantOptions CreateOptions(List<string> calls)
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
