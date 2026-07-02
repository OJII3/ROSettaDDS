using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl.Naming;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// rosettadds の Domain Participant。SPDP / SEDP / ユーザートピック transport を一元管理する。
/// </summary>
public sealed class DomainParticipant : IDisposable
{
    private readonly DomainParticipantOptions _options;
    private readonly ParticipantTransportSet _transports;
    private readonly ParticipantRtpsReceiver _receiver;
    private readonly DiscoveryDb _discoveryDb;
    private readonly LeaseExpiryMonitor _leaseExpiryMonitor;
    private readonly SpdpBuiltinParticipantReader _spdpReader;
    private readonly SpdpBuiltinParticipantWriter _spdpWriter;
    private readonly SedpEndpointWriter _sedpPublicationsWriter;
    private readonly SedpEndpointReader _sedpPublicationsReader;
    private readonly SedpEndpointWriter _sedpSubscriptionsWriter;
    private readonly SedpEndpointReader _sedpSubscriptionsReader;
    private readonly SedpEndpointAdvertiser _sedpAdvertiser;
    private readonly UserEntityIdAllocator _userEntityIds = new();
    private readonly ParticipantEndpointFactory _endpointFactory;
    private readonly UserEndpointManager _userEndpoints;

    private bool _started;
    private bool _disposed;
    private bool _unregisteringLocalEndpoints;

    public DomainParticipantOptions Options => _options;
    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public DiscoveryDb DiscoveryDb => _discoveryDb;

    /// <summary>実際に使用された Participant ID。auto-probe により入力値と異なる場合がある。</summary>
    public int ResolvedParticipantId => _transports.ResolvedParticipantId;

    /// <summary>ユーザートピックの multicast 送受信に使うトランスポート。</summary>
    public IRtpsTransport UserMulticastTransport => _transports.UserMulticast;

    /// <summary>ユーザートピックの unicast 送受信に使うトランスポート。</summary>
    public IRtpsTransport UserUnicastTransport => _transports.UserUnicast;

    /// <summary>ユーザートピックの multicast 送信先 Locator。</summary>
    public Locator UserMulticastDestination => _transports.UserMulticastDestination;

    public DomainParticipant(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);

        _transports = ParticipantTransportSet.Create(Rcl.ContextOptions.FromLegacy(_options));
        _receiver = new ParticipantRtpsReceiver(GuidPrefix, _options.Logger);

        _discoveryDb = new DiscoveryDb(_options.DiscoveryLimits);
        _leaseExpiryMonitor = new LeaseExpiryMonitor(_discoveryDb, Rcl.ContextOptions.FromLegacy(_options), _options.Logger);
        _userEndpoints = new UserEndpointManager(_discoveryDb, new ParticipantRtpsReceiverAdapter(_receiver), _options.Logger);
        _endpointFactory = new ParticipantEndpointFactory(
            Rcl.ContextOptions.FromLegacy(_options),
            _transports,
            GuidPrefix,
            Guid,
            _userEntityIds);

        _spdpReader = new SpdpBuiltinParticipantReader(
            _transports.MetatrafficMulticast, _discoveryDb, GuidPrefix, _options.Logger, limits: _options.DiscoveryLimits);

        _spdpWriter = new SpdpBuiltinParticipantWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            participantDataProvider: BuildParticipantData,
            interval: _options.SpdpInterval,
            logger: _options.Logger);

        // SEDP: Writer は multicast transport で送信、unicast transport で ACKNACK を受信。
        // Reader は multicast / unicast 両方で DATA/HB を受信、unicast で ACKNACK を返信。
        _sedpPublicationsWriter = new SedpEndpointWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpPublicationsReader = new SedpEndpointReader(
            replyTransport: _transports.MetatrafficUnicast,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsReader,
            ackNackFallbackDestination: _transports.MetatrafficMulticastDestination,
            producedEndpointKind: EndpointKind.Writer,
            logger: _options.Logger,
            limits: _options.DiscoveryLimits);

        _sedpSubscriptionsWriter = new SedpEndpointWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpSubscriptionsReader = new SedpEndpointReader(
            replyTransport: _transports.MetatrafficUnicast,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsReader,
            ackNackFallbackDestination: _transports.MetatrafficMulticastDestination,
            producedEndpointKind: EndpointKind.Reader,
            logger: _options.Logger,
            limits: _options.DiscoveryLimits);

        _sedpAdvertiser = new SedpEndpointAdvertiser(
            _options.Logger,
            () => _leaseExpiryMonitor.CancellationToken,
            () => _disposed);

        // SPDP で remote participant を発見/更新したら SEDP endpoint を auto-match
        _discoveryDb.ParticipantDiscovered += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantUpdated += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantLost += OnRemoteParticipantLost;

        // SEDP で remote reader を発見したらローカル writer にユニキャストロケータを追加
        _discoveryDb.ReaderDiscovered += OnRemoteReaderDiscovered;

        // SEDP で remote writer を発見したらローカル subscription の受信対象に追加
        _discoveryDb.WriterDiscovered += OnRemoteWriterDiscovered;

        _discoveryDb.EndpointUpdated += OnRemoteEndpointUpdated;
        _discoveryDb.ReaderLost += OnRemoteReaderLost;
        _discoveryDb.WriterLost += OnRemoteWriterLost;

        // builtin endpoint を単一 receiver のルーティング対象として登録する。
        // reader は DATA/HB/GAP の宛先、writer は ACKNACK の宛先として EntityId で引かれる。
        _receiver.RegisterReader(BuiltinEntityIds.SpdpBuiltinParticipantReader, _spdpReader);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinPublicationsReader, _sedpPublicationsReader.Stateful);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinSubscriptionsReader, _sedpSubscriptionsReader.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinPublicationsWriter, _sedpPublicationsWriter.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinSubscriptionsWriter, _sedpSubscriptionsWriter.Stateful);
    }

    private void OnRemoteParticipantDiscovered(RemoteParticipant participant)
    {
        // remote SEDP endpoint の Guid を計算 (固定 EntityId)
        var prefix = participant.GuidPrefix;
        var remoteSedpPubReader = new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsReader);
        var remoteSedpPubWriter = new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsWriter);
        var remoteSedpSubReader = new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsReader);
        var remoteSedpSubWriter = new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsWriter);

        // remote の metatraffic unicast (ACKNACK 返送先 / DATA 送信先)
        Locator? remoteUnicast = participant.Data.MetatrafficUnicastLocators.Count > 0
            ? participant.Data.MetatrafficUnicastLocators[0]
            : null;

        // 自 writer ↔ remote reader
        _sedpPublicationsWriter.MatchRemoteReader(remoteSedpPubReader, remoteUnicast);
        _sedpSubscriptionsWriter.MatchRemoteReader(remoteSedpSubReader, remoteUnicast);

        // 自 reader ↔ remote writer (ACKNACK 返送先として remoteUnicast)
        _sedpPublicationsReader.MatchRemoteWriter(remoteSedpPubWriter, remoteUnicast);
        _sedpSubscriptionsReader.MatchRemoteWriter(remoteSedpSubWriter, remoteUnicast);

        _options.Logger.Debug($"DomainParticipant: auto-matched SEDP endpoints for {participant.Guid}");
    }

    private void OnRemoteParticipantLost(RemoteParticipant participant)
    {
        var prefix = participant.GuidPrefix;
        _sedpPublicationsWriter.UnmatchRemoteReader(new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsReader));
        _sedpSubscriptionsWriter.UnmatchRemoteReader(new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsReader));
        _sedpPublicationsReader.UnmatchRemoteWriter(new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsWriter));
        _sedpSubscriptionsReader.UnmatchRemoteWriter(new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsWriter));

        _options.Logger.Debug($"DomainParticipant: unmatched SEDP endpoints for lost participant {participant.Guid}");
    }

    private void OnRemoteReaderDiscovered(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderChanged(remoteReader);

    private void OnRemoteWriterDiscovered(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterChanged(remoteWriter);

    private void OnRemoteEndpointUpdated(RemoteEndpoint remoteEndpoint)
    {
        if (remoteEndpoint.Kind == EndpointKind.Reader)
        {
            OnRemoteReaderDiscovered(remoteEndpoint);
        }
        else
        {
            OnRemoteWriterDiscovered(remoteEndpoint);
        }
    }

    private void OnRemoteReaderLost(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderLost(remoteReader);

    private void OnRemoteWriterLost(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterLost(remoteWriter);

    /// <summary>送受信トランスポートと SPDP の起動。</summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }
        _transports.Start();

        // participant 単位の単一 receiver が全 transport の受信を 1 経路に集約し、
        // パケットを 1 回だけパースして submessage を宛先 endpoint へ fan-out する。
        // (SPDP/SEDP/user の各 reader・writer は constructor / endpoint 生成時に登録済み)
        _receiver.Subscribe(_transports.MetatrafficMulticast);
        _receiver.Subscribe(_transports.MetatrafficUnicast);
        _receiver.Subscribe(_transports.UserMulticast);
        _receiver.Subscribe(_transports.UserUnicast);

        _spdpWriter.Start();
        _sedpPublicationsWriter.Start();
        _sedpSubscriptionsWriter.Start();
        _userEndpoints.StartWriters();
        _leaseExpiryMonitor.Start();
        _started = true;
    }

    /// <summary>SPDP / SEDP / Transport を停止する。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _leaseExpiryMonitor.Stop();
        _userEndpoints.StopWriters();
        _sedpPublicationsWriter.Stop();
        _sedpSubscriptionsWriter.Stop();
        _receiver.UnsubscribeAll();
        _spdpWriter.Stop();
        _transports.Stop();
        _started = false;
    }

    /// <summary>現在の自 Participant の <see cref="ParticipantData"/> を生成する (SPDP 送信時に使われる)。</summary>
    public ParticipantData BuildParticipantData()
    {
        var data = new ParticipantData
        {
            ProtocolVersion = _options.ProtocolVersion,
            VendorId = _options.VendorId,
            Guid = Guid,
            BuiltinEndpoints = BuiltinEndpointSet.ROSettaDDSDefault,
            LeaseDuration = _options.LeaseDuration,
            EntityName = _options.EntityName,
        };
        data.MetatrafficMulticastLocators.Add(_transports.MetatrafficMulticastDestination);
        data.MetatrafficUnicastLocators.AddRange(_transports.MetatrafficUnicastLocators);
        data.DefaultUnicastLocators.AddRange(_transports.DefaultUnicastLocators);
        data.DefaultMulticastLocators.Add(_transports.UserMulticastDestination);
        return data;
    }

    /// <summary>
    /// 指定トピックの Publisher を生成する。
    /// EntityId は <see cref="UserEntityIdAllocator"/> により Participant 内の連番で割り当てる。
    /// 同時にローカル endpoint 一覧へ登録され、SEDP で広告される。
    /// </summary>
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => CreatePublisher(topicName, serializer, ReliabilityQos.Reliable, DurabilityQos.Volatile, typeName);

    /// <summary>
    /// 指定トピックの Publisher を生成する。
    /// <paramref name="reliability"/> / <paramref name="durability"/> は SEDP で広告する QoS として使われる。
    /// <paramref name="durability"/> が TransientLocal の場合、後発でマッチした reader に history を再送する。
    /// </summary>
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
            token => _sedpPublicationsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local writer endpoint");

        var pub = new Publisher<T>(userTopicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
    }

    /// <summary>
    /// 指定サービス名のクライアントを生成する。request は "rq/&lt;name&gt;Request"、
    /// reply は "rr/&lt;name&gt;Reply" に対応する。QoS は ROS 2 services 既定 (Reliable/Volatile)。
    /// </summary>
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
            requestPublisher, replyReader, descriptor, _options.Logger, _options.CdrReadLimits);
    }

    /// <summary>
    /// 指定トピックの Subscription を生成する。
    /// 受信ループは即座に開始され、マッチする DATA を受信するとハンドラが呼ばれる。
    /// 同時にローカル endpoint 一覧へ登録され、SEDP で広告される。
    /// </summary>
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

        // 既定は ROS 2 と同じ Reliable。Reliable は StatefulReader (ACKNACK 返送)、
        // BestEffort は StatelessReader 経路へ接続する。
        var effectiveReliability = reliability ?? ReliabilityQos.Reliable;
        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var endpoint = _endpointFactory.CreateReader(ddsTopic, serializer, effectiveReliability, typeName);
        var reader = endpoint.Reader;
        var endpointGuid = endpoint.EndpointGuid;
        var endpointData = endpoint.EndpointData;

        // Subscription を先に生成して PayloadReceived を購読してから reader を receiver へ登録する。
        // 逆順だと、登録直後に届く writer の (TransientLocal) 履歴再送を取りこぼす競合がある。
        var subscription = new Subscription<T>(
            topicName,
            endpointGuid,
            reader,
            serializer,
            handler,
            UnregisterLocalReader,
            handlerContext,
            _options.Logger,
            cdrReadLimits: _options.CdrReadLimits);

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => _sedpSubscriptionsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local reader endpoint");

        return subscription;
    }

    /// <summary>ハンドラが GuidPrefix を必要としない場合のショートカット。</summary>
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

    /// <summary>
    /// Reliable reader を生成し、SEDP 広告と receiver/UserEndpointManager 登録まで行う。
    /// サービス reply 用に具象 <see cref="ReliableUserReader"/> を返す。
    /// </summary>
    /// <param name="ddsTopic">既に mangle 済みの DDS トピック名 (例 "rr/add_two_intsReply")。</param>
    /// <param name="ddsTypeName">DDS 型名 (例 "example_interfaces::srv::dds_::AddTwoInts_Response_")。</param>
    private ReliableUserReader CreateReliableReplyReaderInternal(string ddsTopic, string ddsTypeName)
    {
        ThrowIfDisposed();
        var endpoint = _endpointFactory.CreateReliableReplyReader(ddsTopic, ddsTypeName);
        var reader = endpoint.Reader;
        var endpointData = endpoint.EndpointData;

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => _sedpSubscriptionsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local service reply reader endpoint");
        return reader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        UnregisterAllLocalEndpoints();
        Stop();
        _sedpPublicationsWriter.Dispose();
        _sedpSubscriptionsWriter.Dispose();
        _sedpPublicationsReader.Dispose();
        _sedpSubscriptionsReader.Dispose();
        _spdpWriter.Dispose();
        _spdpReader.Dispose();
        _leaseExpiryMonitor.Dispose();
        _receiver.Dispose();
        _transports.Dispose();
    }

    private void UnregisterAllLocalEndpoints()
    {
        if (_unregisteringLocalEndpoints)
        {
            return;
        }
        _unregisteringLocalEndpoints = true;
        try
        {
            var endpoints = _userEndpoints.Snapshot();
            foreach (var writer in endpoints.Writers)
            {
                UnregisterLocalWriter(writer.Guid, writer);
                writer.Dispose();
            }
            foreach (var reader in endpoints.Readers)
            {
                var readerGuid = new Guid(GuidPrefix, reader.ReaderEntityId);
                UnregisterLocalReader(readerGuid, reader);
                reader.Dispose();
            }
        }
        finally
        {
            _unregisteringLocalEndpoints = false;
        }
    }

    private void UnregisterLocalWriter(Guid endpointGuid, StatefulWriter writerToRemove)
    {
        var result = _userEndpoints.UnregisterWriter(endpointGuid, writerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(_sedpPublicationsWriter.UnregisterEndpointAsync(result.Endpoint!));
        }
    }

    private void UnregisterLocalReader(Guid endpointGuid, IUserReader readerToRemove)
    {
        var result = _userEndpoints.UnregisterReader(endpointGuid, readerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(_sedpSubscriptionsWriter.UnregisterEndpointAsync(result.Endpoint!));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

}
