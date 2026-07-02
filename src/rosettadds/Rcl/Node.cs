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

/// <summary>
/// ROS 2 の rcl_node_t (rclcpp::Node) 相当。<see cref="Context"/> を参照し、
/// Publisher / Subscription / ServiceClient のみを生らす薄いラッパ。
/// </summary>
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

        var discovery = context.DiscoveryDb;
        discovery.ReaderDiscovered += OnRemoteReaderDiscovered;
        discovery.WriterDiscovered += OnRemoteWriterDiscovered;
        discovery.EndpointUpdated += OnRemoteEndpointUpdated;
        discovery.ReaderLost += OnRemoteReaderLost;
        discovery.WriterLost += OnRemoteWriterLost;

        context.RegisterNode(this);
    }

    public string Name { get; }
    public Context Context { get; }
    public NodeOptions Options => _options;

    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => CreatePublisher(topicName, serializer, ReliabilityQos.Reliable, DurabilityQos.Volatile, typeName);

    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        return CreateWriterInternal(
            TopicNameMangler.MangleTopic(topicName), serializer, reliability, durability,
            typeName, topicName);
    }

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var effectiveReliability = reliability ?? ReliabilityQos.Reliable;
        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var endpoint = _endpointFactory.CreateReader(ddsTopic, serializer, effectiveReliability, typeName);
        var reader = endpoint.Reader;
        var endpointGuid = endpoint.EndpointGuid;
        var endpointData = endpoint.EndpointData;

        var subscription = new Subscription<T>(
            topicName,
            endpointGuid,
            reader,
            serializer,
            handler,
            UnregisterLocalReader,
            handlerContext,
            Context.Logger,
            cdrReadLimits: Context.Options.CdrReadLimits);

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddSubscriptionAsync(endpointData, token),
            "Node failed to advertise local reader endpoint");

        return subscription;
    }

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => CreateSubscription<T>(
            topicName,
            serializer,
            (value, _) => handler(value),
            handlerContext: handlerContext,
            reliability: reliability);

    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
    {
        ThrowIfDisposed();
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Value cannot be null or empty.", nameof(serviceName));

        var requestPublisher = CreateWriterInternal(
            TopicNameMangler.MangleServiceRequest(serviceName),
            descriptor.RequestSerializer,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile,
            typeName: descriptor.RequestDdsTypeName,
            userTopicName: serviceName);

        var replyReader = CreateReliableReplyReaderInternal(
            TopicNameMangler.MangleServiceReply(serviceName),
            descriptor.ResponseDdsTypeName);

        return new ServiceClient<TRequest, TResponse>(
            requestPublisher, replyReader, descriptor, Context.Logger, Context.Options.CdrReadLimits);
    }

    private Publisher<T> CreateWriterInternal<T>(
        string ddsTopic,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName,
        string userTopicName)
    {
        var endpoint = _endpointFactory.CreateWriter(ddsTopic, serializer, reliability, durability, typeName);
        var writer = endpoint.Writer;
        var endpointData = endpoint.EndpointData;
        _userEndpoints.RegisterWriter(endpointData, writer);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddPublicationAsync(endpointData, token),
            "Node failed to advertise local writer endpoint");

        var pub = new Publisher<T>(userTopicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
    }

    private ReliableUserReader CreateReliableReplyReaderInternal(string ddsTopic, string ddsTypeName)
    {
        var endpoint = _endpointFactory.CreateReliableReplyReader(ddsTopic, ddsTypeName);
        var reader = endpoint.Reader;
        var endpointData = endpoint.EndpointData;

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddSubscriptionAsync(endpointData, token),
            "Node failed to advertise local service reply reader endpoint");
        return reader;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAllLocalEndpoints();
        Context.UnregisterNode(this);
    }

    private void UnregisterAllLocalEndpoints()
    {
        var endpoints = _userEndpoints.Snapshot();
        foreach (var writer in endpoints.Writers)
        {
            UnregisterLocalWriter(writer.Guid, writer);
            writer.Dispose();
        }
        foreach (var reader in endpoints.Readers)
        {
            var readerGuid = new Guid(Context.GuidPrefix, reader.ReaderEntityId);
            UnregisterLocalReader(readerGuid, reader);
            reader.Dispose();
        }
    }

    private void UnregisterLocalWriter(Guid endpointGuid, StatefulWriter writerToRemove)
    {
        var result = _userEndpoints.UnregisterWriter(endpointGuid, writerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(Context.UnregisterPublicationAsync(result.Endpoint!));
        }
    }

    private void UnregisterLocalReader(Guid endpointGuid, IUserReader readerToRemove)
    {
        var result = _userEndpoints.UnregisterReader(endpointGuid, readerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(Context.UnregisterSubscriptionAsync(result.Endpoint!));
        }
    }

    private void OnRemoteReaderDiscovered(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderChanged(remoteReader);

    private void OnRemoteWriterDiscovered(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterChanged(remoteWriter);

    private void OnRemoteEndpointUpdated(RemoteEndpoint remoteEndpoint)
    {
        if (remoteEndpoint.Kind == EndpointKind.Reader)
            OnRemoteReaderDiscovered(remoteEndpoint);
        else
            OnRemoteWriterDiscovered(remoteEndpoint);
    }

    private void OnRemoteReaderLost(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderLost(remoteReader);

    private void OnRemoteWriterLost(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterLost(remoteWriter);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
