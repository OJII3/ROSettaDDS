using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// ROSettaDDS の Domain Participant (旧公開エントリポイント)。
/// 内部では <see cref="Rcl.Context"/> + <see cref="Rcl.Node"/> を生成して委譲する。
/// 新規コードでは <see cref="Rcl.Context"/> + <see cref="Rcl.Node"/> を使うこと。
/// </summary>
[Obsolete("Use ROSettaDDS.Rcl.Context + ROSettaDDS.Rcl.Node instead. " +
          "DomainParticipant will be removed in a future release.")]
public sealed class DomainParticipant : IDisposable
{
    private readonly DomainParticipantOptions _options;
    private readonly Rcl.Context _context;
    private readonly Rcl.Node _node;

    public DomainParticipant(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;
        var ctxOpts = Rcl.ContextOptions.FromLegacy(options);
        _context = new Rcl.Context(ctxOpts);
        _node = new Rcl.Node(_context, options.EntityName);
    }

    public Guid Guid => _context.Guid;
    public GuidPrefix GuidPrefix => _context.GuidPrefix;
    public DiscoveryDb DiscoveryDb => _context.DiscoveryDb;
    public DomainParticipantOptions Options => _options;
    public int ResolvedParticipantId => _context.ResolvedParticipantId;
    public IRtpsTransport UserMulticastTransport => _context.UserMulticastTransport;
    public IRtpsTransport UserUnicastTransport => _context.UserUnicastTransport;
    public Locator UserMulticastDestination => _context.UserMulticastDestination;

    public void Start() => _context.Start();
    public void Stop() => _context.Stop();
    public void Dispose() { _node.Dispose(); _context.Dispose(); }

    [Obsolete("Use Node.CreatePublisher instead.")]
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => _node.CreatePublisher<T>(topicName, serializer, typeName);

    [Obsolete("Use Node.CreatePublisher instead.")]
    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
        => _node.CreatePublisher<T>(topicName, serializer, reliability, durability, typeName);

    [Obsolete("Use Node.CreateSubscription instead.")]
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => _node.CreateSubscription<T>(topicName, serializer, handler, typeName, handlerContext, reliability);

    [Obsolete("Use Node.CreateSubscription instead.")]
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => _node.CreateSubscription<T>(topicName, serializer, handler, handlerContext, reliability);

    [Obsolete("Use Node.CreateServiceClient instead.")]
    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
        => _node.CreateServiceClient<TRequest, TResponse>(descriptor, serviceName);
}
