using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl.Naming;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class ParticipantEndpointFactoryTests
{
    [Fact]
    public void CreateWriter_は_endpoint_dataにQoSとlocatorを設定する()
    {
        var options = CreateOptions();
        using var transports = ParticipantTransportSet.Create(options);
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var factory = new ParticipantEndpointFactory(
            options,
            transports,
            prefix,
            new Guid(prefix, BuiltinEntityIds.Participant),
            new UserEntityIdAllocator());

        var result = factory.CreateWriter<StringMessage>(
            "rt/chatter",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile,
            StringMessage.DdsTypeName);

        result.EndpointData.Kind.Should().Be(EndpointKind.Writer);
        result.EndpointData.EndpointGuid.Prefix.Should().Be(prefix);
        result.EndpointData.EndpointGuid.EntityId.Kind.Should().Be(EntityKind.UserDefinedWriterNoKey);
        result.EndpointData.TopicName.Should().Be("rt/chatter");
        result.EndpointData.TypeName.Should().Be(StringMessage.DdsTypeName);
        result.EndpointData.Reliability.Kind.Should().Be(ReliabilityKind.BestEffort);
        result.EndpointData.UnicastLocators.Should().ContainSingle()
            .Which.Port.Should().Be(7411);
        result.EndpointData.MulticastLocators.Should().ContainSingle()
            .Which.Port.Should().Be(7401);
    }

    private static DomainParticipantOptions CreateOptions()
        => new()
        {
            CustomMulticastTransport = new RecordingTransport(7400),
            CustomUnicastTransport = new RecordingTransport(7410),
            CustomUserMulticastTransport = new RecordingTransport(7401),
            CustomUserUnicastTransport = new RecordingTransport(7411),
        };

    private sealed class RecordingTransport : IRtpsTransport
    {
        public RecordingTransport(uint port)
        {
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

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
