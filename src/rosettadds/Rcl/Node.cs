using System.Threading;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl.Naming;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rcl;

public sealed class Node : IDisposable
{
    private readonly NodeOptions _options;
    private readonly UserEntityIdAllocator _userEntityIds = new();
    private readonly ParticipantEndpointFactory _endpointFactory;
    private readonly UserEndpointManager _userEndpoints;
    private readonly SedpEndpointAdvertiser _sedpAdvertiser;
    private bool _disposed;

    public Node(Context context, string name, NodeOptions? options = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Value cannot be null or empty.", nameof(name));
        Context = context;
        Name = name;
        _options = options ?? NodeOptions.Default;

        _endpointFactory = new ParticipantEndpointFactory(
            context.Options,
            context.Transports,
            context.GuidPrefix,
            context.Guid,
            _userEntityIds);

        _userEndpoints = new UserEndpointManager(
            context.DiscoveryDb,
            new ParticipantRtpsReceiverAdapter(context.Receiver),
            context.Logger);

        _sedpAdvertiser = new SedpEndpointAdvertiser(
            context.Logger,
            () => context.LeaseExpiryCancellationToken,
            () => _disposed);

        context.RegisterNode(this);
    }

    public string Name { get; }
    public Context Context { get; }
    public NodeOptions Options => _options;

    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => throw new NotImplementedException();

    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
        => throw new NotImplementedException();

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => throw new NotImplementedException();

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => throw new NotImplementedException();

    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
        => throw new NotImplementedException();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Context.UnregisterNode(this);
    }
}
