using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Dds;

public class QosCompatibilityTests
{
    private static DiscoveredEndpointData MakeEndpoint(
        ReliabilityKind reliability = ReliabilityKind.BestEffort,
        DurabilityKind durability = DurabilityKind.Volatile)
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        return new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = new Guid(prefix, new EntityId(1, EntityKind.UserDefinedWriterNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = "rt/test",
            TypeName = "test_type",
            Reliability = new ReliabilityQos(reliability, Duration.Zero),
            Durability = new DurabilityQos(durability),
        };
    }

    // --- Reliability RxO ---

    [Fact]
    public void Reliable_writer_と_BestEffort_reader_は互換()
    {
        var writer = MakeEndpoint(reliability: ReliabilityKind.Reliable);
        var reader = MakeEndpoint(reliability: ReliabilityKind.BestEffort);
        QosCompatibility.IsCompatible(writer, reader).Should().BeTrue();
    }

    [Fact]
    public void Reliable_writer_と_Reliable_reader_は互換()
    {
        var writer = MakeEndpoint(reliability: ReliabilityKind.Reliable);
        var reader = MakeEndpoint(reliability: ReliabilityKind.Reliable);
        QosCompatibility.IsCompatible(writer, reader).Should().BeTrue();
    }

    [Fact]
    public void BestEffort_writer_と_BestEffort_reader_は互換()
    {
        var writer = MakeEndpoint(reliability: ReliabilityKind.BestEffort);
        var reader = MakeEndpoint(reliability: ReliabilityKind.BestEffort);
        QosCompatibility.IsCompatible(writer, reader).Should().BeTrue();
    }

    [Fact]
    public void BestEffort_writer_と_Reliable_reader_は非互換()
    {
        var writer = MakeEndpoint(reliability: ReliabilityKind.BestEffort);
        var reader = MakeEndpoint(reliability: ReliabilityKind.Reliable);
        QosCompatibility.IsCompatible(writer, reader).Should().BeFalse();
    }

    // --- Durability RxO ---

    [Fact]
    public void TransientLocal_writer_と_Volatile_reader_は互換()
    {
        var writer = MakeEndpoint(durability: DurabilityKind.TransientLocal);
        var reader = MakeEndpoint(durability: DurabilityKind.Volatile);
        QosCompatibility.IsCompatible(writer, reader).Should().BeTrue();
    }

    [Fact]
    public void Volatile_writer_と_TransientLocal_reader_は非互換()
    {
        var writer = MakeEndpoint(durability: DurabilityKind.Volatile);
        var reader = MakeEndpoint(durability: DurabilityKind.TransientLocal);
        QosCompatibility.IsCompatible(writer, reader).Should().BeFalse();
    }

    // --- Reliability と Durability の複合 ---

    [Fact]
    public void Reliability不一致で非互換ならDurabilityは無関係()
    {
        // Reliability が非互換なら Durability が互換でも全体として非互換
        var writer = MakeEndpoint(reliability: ReliabilityKind.BestEffort, durability: DurabilityKind.Persistent);
        var reader = MakeEndpoint(reliability: ReliabilityKind.Reliable, durability: DurabilityKind.Volatile);
        QosCompatibility.IsCompatible(writer, reader).Should().BeFalse();
    }

    [Fact]
    public void Durability不一致で非互換ならReliabilityが互換でも全体として非互換()
    {
        var writer = MakeEndpoint(reliability: ReliabilityKind.Reliable, durability: DurabilityKind.Volatile);
        var reader = MakeEndpoint(reliability: ReliabilityKind.Reliable, durability: DurabilityKind.TransientLocal);
        QosCompatibility.IsCompatible(writer, reader).Should().BeFalse();
    }
}
