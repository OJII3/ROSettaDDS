using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Discovery;

/// <summary>
/// SEDP で検出した remote endpoint (Writer または Reader)。
/// </summary>
public sealed class RemoteEndpoint
{
    public DiscoveredEndpointData Data { get; private set; }
    public DateTime FirstSeenUtc { get; }
    public DateTime LastSeenUtc { get; private set; }

    public Guid Guid => Data.EndpointGuid;
    public Guid ParticipantGuid => Data.ParticipantGuid;
    public EndpointKind Kind => Data.Kind;
    public string TopicName => Data.TopicName;
    public string TypeName => Data.TypeName;

    public RemoteEndpoint(DiscoveredEndpointData data, DateTime nowUtc)
    {
        Data = data;
        FirstSeenUtc = nowUtc;
        LastSeenUtc = nowUtc;
    }

    public void Update(DiscoveredEndpointData data, DateTime nowUtc)
    {
        Data = data;
        LastSeenUtc = nowUtc;
    }

    public override string ToString()
        => $"RemoteEndpoint({Kind} {Guid} topic={TopicName} type={TypeName})";
}
