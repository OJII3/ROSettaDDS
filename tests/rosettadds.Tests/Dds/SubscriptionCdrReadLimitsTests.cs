using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rtps.Reader;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Dds;

public class SubscriptionCdrReadLimitsTests
{
    [Fact]
    public void 既定CDR上限は1Mi要素を超えるByteMultiArrayを読める()
    {
        const int dataLength = 1_048_577;
        var data = new byte[dataLength];
        data[0] = 0x42;
        data[^1] = 0x24;
        var payload = SerializeWithEncapsulation(
            ByteMultiArraySerializer.Instance,
            new ByteMultiArray(new MultiArrayLayout([], 0), data));

        using var sub = CreateSubscription(ByteMultiArraySerializer.Instance);

        var value = sub.DeserializeWithEncapsulation(payload.Span);

        value.Data.Should().HaveCount(dataLength);
        value.Data[0].Should().Be(0x42);
        value.Data[^1].Should().Be(0x24);
    }

    [Fact]
    public void Subscriptionは指定されたCDR上限でuser_payloadを拒否する()
    {
        var payload = SerializeWithEncapsulation(
            ByteMultiArraySerializer.Instance,
            new ByteMultiArray(new MultiArrayLayout([], 0), [1, 2, 3]));

        using var sub = CreateSubscription(
            ByteMultiArraySerializer.Instance,
            new CdrReadLimits(maxSequenceBytes: 2));

        Assert.Throws<InvalidDataException>(() => sub.DeserializeWithEncapsulation(payload.Span));
    }

    [Fact]
    public void DomainParticipantOptionsのCDR上限がSubscriptionへ渡る()
    {
        var payload = SerializeWithEncapsulation(
            ByteMultiArraySerializer.Instance,
            new ByteMultiArray(new MultiArrayLayout([], 0), [1, 2, 3]));
        using var participant = CreateParticipant(new CdrReadLimits(maxSequenceBytes: 2));
        using var sub = participant.CreateSubscription<ByteMultiArray>(
            "limited_bytes",
            ByteMultiArraySerializer.Instance,
            _ => { });

        Assert.Throws<InvalidDataException>(() => sub.DeserializeWithEncapsulation(payload.Span));
    }

    private static Subscription<T> CreateSubscription<T>(
        ICdrSerializer<T> serializer,
        CdrReadLimits? limits = null)
    {
        var reader = new BestEffortUserReader(GuidPrefix.Unknown, new EntityId(1, EntityKind.UserDefinedReaderNoKey));
        return new Subscription<T>(
            "test_topic",
            default,
            reader,
            serializer,
            (_, _) => { },
            cdrReadLimits: limits,
            autoStart: false);
    }

    private static DomainParticipant CreateParticipant(CdrReadLimits limits)
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        return new DomainParticipant(new DomainParticipantOptions
        {
            DomainId = 0,
            ParticipantId = 30,
            EntityName = "cdr_limits",
            MulticastGroup = multicastIp,
            CdrReadLimits = limits,
            CustomMulticastTransport = hub.Create(spdpLoc),
            CustomUnicastTransport = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.30"), 7461u)),
            CustomUserMulticastTransport = hub.Create(userMcLoc),
            CustomUserUnicastTransport = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.30"), 7462u)),
        });
    }

    private static ReadOnlyMemory<byte> SerializeWithEncapsulation<T>(
        ICdrSerializer<T> serializer,
        in T value)
    {
        int sizeEstimate = serializer.GetSerializedSize(value);
        var buffer = new byte[CdrEncapsulation.Size + sizeEstimate + 16];
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var writer = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        serializer.Serialize(ref writer, in value);
        return buffer.AsMemory(0, writer.Position);
    }
}
