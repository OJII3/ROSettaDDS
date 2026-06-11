using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Dds.QoS;
using Rclsharp.Discovery;
using Rclsharp.Rcl.Naming;
using Rclsharp.Rtps.Reader;
using Rclsharp.Rtps.Writer;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Dds;

/// <summary>
/// rclsharp の Domain Participant。SPDP / SEDP / ユーザートピック transport を一元管理する。
/// </summary>
public sealed class DomainParticipant : IDisposable
{
    private static readonly TimeSpan MaxLeaseExpiryCheckPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinLeaseExpiryCheckPeriod = TimeSpan.FromMilliseconds(50);

    private readonly DomainParticipantOptions _options;
    private readonly ParticipantTransportSet _transports;
    private readonly DiscoveryDb _discoveryDb;
    private readonly SpdpBuiltinParticipantReader _spdpReader;
    private readonly SpdpBuiltinParticipantWriter _spdpWriter;
    private readonly SedpEndpointWriter _sedpPublicationsWriter;
    private readonly SedpEndpointReader _sedpPublicationsReader;
    private readonly SedpEndpointWriter _sedpSubscriptionsWriter;
    private readonly SedpEndpointReader _sedpSubscriptionsReader;
    private readonly TimeSpan _leaseExpiryCheckPeriod;
    private readonly UserEntityIdAllocator _userEntityIds = new();

    // ローカル endpoint 一覧 (SEDP 送信時に使用)
    private readonly object _localEndpointsLock = new();
    private readonly List<DiscoveredEndpointData> _localWriters = new();
    private readonly List<DiscoveredEndpointData> _localReaders = new();

    // ローカル StatefulWriter をトピック名でルックアップ (remote reader マッチング用)
    private readonly Dictionary<string, List<LocalUserWriter>> _localUserWriters = new();

    // ローカル StatelessReader をトピック名でルックアップ (remote writer マッチング用)
    private readonly Dictionary<string, List<LocalUserReader>> _localUserReaders = new();
    private StatefulWriter[] _localUserWriterSnapshot = Array.Empty<StatefulWriter>();
    private StatelessReader[] _localUserReaderSnapshot = Array.Empty<StatelessReader>();

    private bool _started;
    private bool _disposed;
    private bool _unregisteringLocalEndpoints;
    private CancellationTokenSource? _leaseExpiryCts;
    private Task? _leaseExpiryLoop;

    public DomainParticipantOptions Options => _options;
    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public DiscoveryDb DiscoveryDb => _discoveryDb;

    /// <summary>ユーザートピックの multicast 送受信に使うトランスポート。</summary>
    public IRtpsTransport UserMulticastTransport => _transports.UserMulticast;

    /// <summary>ユーザートピックの unicast 送受信に使うトランスポート。</summary>
    public IRtpsTransport UserUnicastTransport => _transports.UserUnicast;

    /// <summary>ユーザートピックの multicast 送信先 Locator。</summary>
    public Locator UserMulticastDestination => _transports.UserMulticastDestination;

    private sealed class LocalUserReader
    {
        public LocalUserReader(DiscoveredEndpointData endpointData, StatelessReader reader)
        {
            EndpointData = endpointData ?? throw new ArgumentNullException(nameof(endpointData));
            Reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public DiscoveredEndpointData EndpointData { get; }
        public StatelessReader Reader { get; }
    }

    private sealed class LocalUserWriter
    {
        public LocalUserWriter(DiscoveredEndpointData endpointData, StatefulWriter writer)
        {
            EndpointData = endpointData ?? throw new ArgumentNullException(nameof(endpointData));
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public DiscoveredEndpointData EndpointData { get; }
        public StatefulWriter Writer { get; }
    }

    public DomainParticipant(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;
        _leaseExpiryCheckPeriod = ComputeLeaseExpiryCheckPeriod(_options);

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);

        _transports = ParticipantTransportSet.Create(_options);

        _discoveryDb = new DiscoveryDb(_options.DiscoveryLimits);

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
    {
        LocalUserWriter[] writers;
        lock (_localEndpointsLock)
        {
            if (!_localUserWriters.TryGetValue(remoteReader.TopicName, out var list))
            {
                return;
            }
            writers = list.ToArray();
        }

        foreach (var local in writers)
        {
            MatchLocalWriterWithRemoteReader(local, remoteReader);
        }
    }

    private void OnRemoteWriterDiscovered(RemoteEndpoint remoteWriter)
    {
        LocalUserReader[] readers;
        lock (_localEndpointsLock)
        {
            if (!_localUserReaders.TryGetValue(remoteWriter.TopicName, out var list))
            {
                return;
            }
            readers = list.ToArray();
        }

        foreach (var local in readers)
        {
            MatchLocalReaderWithRemoteWriter(local, remoteWriter);
        }
    }

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

    private void MatchLocalWriterWithRemoteReader(LocalUserWriter local, RemoteEndpoint remoteReader)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remoteReader.TypeName))
        {
            local.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
            return;
        }

        var unicastLocator = ResolveRemoteReaderUnicastLocator(remoteReader);
        local.Writer.MatchReader(remoteReader.Data.EndpointGuid, unicastLocator);
        _options.Logger.Debug($"DomainParticipant: matched local writer with remote reader on topic={remoteReader.TopicName} reader={remoteReader.Data.EndpointGuid}");
    }

    private void MatchLocalReaderWithRemoteWriter(LocalUserReader local, RemoteEndpoint remoteWriter)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remoteWriter.TypeName))
        {
            local.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
            return;
        }

        local.Reader.MatchWriter(remoteWriter.Data.EndpointGuid);
        _options.Logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remoteWriter.TopicName} writer={remoteWriter.Data.EndpointGuid}");
    }

    private void MatchLocalReaderWithLocalWriter(LocalUserReader localReader, LocalUserWriter localWriter)
    {
        if (!TypeMatches(localReader.EndpointData.TypeName, localWriter.EndpointData.TypeName))
        {
            localReader.Reader.UnmatchWriter(localWriter.EndpointData.EndpointGuid);
            localWriter.Writer.UnmatchReader(localReader.EndpointData.EndpointGuid);
            return;
        }

        localReader.Reader.MatchWriter(localWriter.EndpointData.EndpointGuid);
        var unicastLocator = FirstUdpLocator(localReader.EndpointData.UnicastLocators);
        localWriter.Writer.MatchReader(localReader.EndpointData.EndpointGuid, unicastLocator);
        _options.Logger.Debug($"DomainParticipant: matched local reader with local writer on topic={localReader.EndpointData.TopicName} writer={localWriter.EndpointData.EndpointGuid}");
    }

    private Locator? ResolveRemoteReaderUnicastLocator(RemoteEndpoint remoteReader)
    {
        var unicastLocator = FirstUdpLocator(remoteReader.Data.UnicastLocators);
        if (unicastLocator is not null)
        {
            return unicastLocator;
        }

        var participants = _discoveryDb.Snapshot();
        foreach (var p in participants)
        {
            if (p.GuidPrefix.Equals(remoteReader.Data.EndpointGuid.Prefix))
            {
                return FirstUdpLocator(p.Data.DefaultUnicastLocators);
            }
        }

        return null;
    }

    private void OnRemoteReaderLost(RemoteEndpoint remoteReader)
    {
        LocalUserWriter[] writers;
        lock (_localEndpointsLock)
        {
            if (!_localUserWriters.TryGetValue(remoteReader.TopicName, out var list))
            {
                return;
            }
            writers = list.ToArray();
        }
        foreach (var local in writers)
        {
            local.Writer.UnmatchReader(remoteReader.Data.EndpointGuid);
        }
    }

    private void OnRemoteWriterLost(RemoteEndpoint remoteWriter)
    {
        LocalUserReader[] readers;
        lock (_localEndpointsLock)
        {
            if (!_localUserReaders.TryGetValue(remoteWriter.TopicName, out var list))
            {
                return;
            }
            readers = list.ToArray();
        }
        foreach (var local in readers)
        {
            local.Reader.UnmatchWriter(remoteWriter.Data.EndpointGuid);
        }
    }

    private void OnUserDataPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        var writers = Volatile.Read(ref _localUserWriterSnapshot);
        var readers = Volatile.Read(ref _localUserReaderSnapshot);
        foreach (var w in writers)
        {
            w.OnPacketReceived(packet, source);
        }
        foreach (var r in readers)
        {
            r.OnPacketReceived(packet, source);
        }
    }

    private static bool TypeMatches(string localTypeName, string remoteTypeName)
        => !string.IsNullOrEmpty(localTypeName)
        && !string.IsNullOrEmpty(remoteTypeName)
        && string.Equals(localTypeName, remoteTypeName, StringComparison.Ordinal);

    private static bool IsUdpLocator(Locator loc)
        => loc.Kind == LocatorKind.UdpV4 || loc.Kind == LocatorKind.UdpV6;

    private static Locator? FirstUdpLocator(IEnumerable<Locator> locators)
    {
        foreach (var loc in locators)
        {
            if (IsUdpLocator(loc)) return loc;
        }
        return null;
    }

    /// <summary>送受信トランスポートと SPDP の起動。</summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }
        _transports.Start();
        _spdpReader.Start();
        _spdpWriter.Start();

        // SEDP の DATA/HB は multicast (初期) と unicast (ACKNACK 返信先) の両方で受信する
        _transports.MetatrafficMulticast.Received += _sedpPublicationsReader.OnPacketReceived;
        _transports.MetatrafficMulticast.Received += _sedpSubscriptionsReader.OnPacketReceived;
        _transports.MetatrafficUnicast.Received += _sedpPublicationsReader.OnPacketReceived;
        _transports.MetatrafficUnicast.Received += _sedpSubscriptionsReader.OnPacketReceived;
        // SEDP writer は ACKNACK を unicast metatraffic で受ける
        _transports.MetatrafficUnicast.Received += _sedpPublicationsWriter.OnPacketReceived;
        _transports.MetatrafficUnicast.Received += _sedpSubscriptionsWriter.OnPacketReceived;

        // ユーザーデータは multicast / unicast の両方を同じ dispatcher で処理する
        _transports.UserMulticast.Received += OnUserDataPacketReceived;
        _transports.UserUnicast.Received += OnUserDataPacketReceived;

        _sedpPublicationsWriter.Start();
        _sedpSubscriptionsWriter.Start();
        StartLocalUserWriters();
        StartLeaseExpiryLoop();
        _started = true;
    }

    /// <summary>SPDP / SEDP / Transport を停止する。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        StopLeaseExpiryLoop();
        StopLocalUserWriters();
        _sedpPublicationsWriter.Stop();
        _sedpSubscriptionsWriter.Stop();
        _transports.UserMulticast.Received -= OnUserDataPacketReceived;
        _transports.UserUnicast.Received -= OnUserDataPacketReceived;
        _transports.MetatrafficMulticast.Received -= _sedpPublicationsReader.OnPacketReceived;
        _transports.MetatrafficMulticast.Received -= _sedpSubscriptionsReader.OnPacketReceived;
        _transports.MetatrafficUnicast.Received -= _sedpPublicationsReader.OnPacketReceived;
        _transports.MetatrafficUnicast.Received -= _sedpSubscriptionsReader.OnPacketReceived;
        _transports.MetatrafficUnicast.Received -= _sedpPublicationsWriter.OnPacketReceived;
        _transports.MetatrafficUnicast.Received -= _sedpSubscriptionsWriter.OnPacketReceived;
        _spdpWriter.Stop();
        _spdpReader.Stop();
        _transports.Stop();
        _started = false;
    }

    private void StartLocalUserWriters()
    {
        LocalUserWriter[] writers;
        lock (_localEndpointsLock)
        {
            writers = _localUserWriters.Values.SelectMany(static list => list).ToArray();
        }

        foreach (var local in writers)
        {
            local.Writer.Start();
        }
    }

    private void StopLocalUserWriters()
    {
        LocalUserWriter[] writers;
        lock (_localEndpointsLock)
        {
            writers = _localUserWriters.Values.SelectMany(static list => list).ToArray();
        }

        foreach (var local in writers)
        {
            local.Writer.Stop();
        }
    }

    private void StartLeaseExpiryLoop()
    {
        if (_leaseExpiryCts is not null)
        {
            return;
        }
        _leaseExpiryCts = new CancellationTokenSource();
        var token = _leaseExpiryCts.Token;
        _leaseExpiryLoop = Task.Run(() => LeaseExpiryLoopAsync(token), token);
    }

    private void StopLeaseExpiryLoop()
    {
        if (_leaseExpiryCts is null)
        {
            return;
        }

        _leaseExpiryCts.Cancel();
        try
        {
            _leaseExpiryLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }
        catch (Exception ex)
        {
            _options.Logger.Warn("DomainParticipant lease expiry loop did not exit cleanly", ex);
        }
        _leaseExpiryCts.Dispose();
        _leaseExpiryCts = null;
        _leaseExpiryLoop = null;
    }

    private async Task LeaseExpiryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_leaseExpiryCheckPeriod, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _discoveryDb.ExpireOldParticipants(DateTime.UtcNow);
        }
    }

    /// <summary>現在の自 Participant の <see cref="ParticipantData"/> を生成する (SPDP 送信時に使われる)。</summary>
    public ParticipantData BuildParticipantData()
    {
        var data = new ParticipantData
        {
            ProtocolVersion = _options.ProtocolVersion,
            VendorId = _options.VendorId,
            Guid = Guid,
            BuiltinEndpoints = BuiltinEndpointSet.RclsharpDefault,
            LeaseDuration = _options.LeaseDuration,
            EntityName = _options.EntityName,
        };
        data.MetatrafficMulticastLocators.Add(_transports.MetatrafficMulticastDestination);
        data.MetatrafficUnicastLocators.Add(_transports.MetatrafficUnicastLocator);
        data.DefaultUnicastLocators.Add(_transports.DefaultUnicastLocator);
        data.DefaultMulticastLocators.Add(_transports.UserMulticastDestination);
        return data;
    }

    /// <summary>
    /// 指定トピックの Publisher を生成する。
    /// EntityId は <see cref="UserEntityIdAllocator"/> により Participant 内の連番で割り当てる。
    /// 同時にローカル endpoint 一覧へ登録され、SEDP で広告される。
    /// </summary>
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => CreatePublisher(topicName, serializer, ReliabilityQos.Reliable, typeName);

    /// <summary>
    /// 指定トピックの Publisher を生成する。
    /// <paramref name="reliability"/> は SEDP で広告する reliability QoS として使われる。
    /// </summary>
    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        string? typeName = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));

        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var ddsTypeName = ResolveDdsTypeName<T>(typeName);
        var writerEntityId = _userEntityIds.AllocateWriter();
        var writerGuid = new Guid(GuidPrefix, writerEntityId);
        var history = new Rtps.HistoryCache.WriterHistoryCache(writerGuid, maxSamples: 1000);
        var writer = new StatefulWriter(
            sendTransport: _transports.UserUnicast,
            multicastDestination: _transports.UserMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: writerEntityId,
            heartbeatPeriod: TimeSpan.FromSeconds(1),
            history: history,
            logger: _options.Logger);

        // SEDP 用に登録 (即時 publish)
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = Guid,
            TopicName = ddsTopic,
            TypeName = ddsTypeName,
            Reliability = reliability,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(_transports.DefaultUnicastLocator);
        endpointData.MulticastLocators.Add(_transports.UserMulticastDestination);
        var localWriter = new LocalUserWriter(endpointData, writer);
        LocalUserReader[] localReaders;
        lock (_localEndpointsLock)
        {
            _localWriters.Add(endpointData);
            if (!_localUserWriters.TryGetValue(ddsTopic, out var writers))
            {
                writers = new List<LocalUserWriter>();
                _localUserWriters[ddsTopic] = writers;
            }
            writers.Add(localWriter);
            RefreshLocalUserEndpointSnapshotsLocked();

            localReaders = _localUserReaders.TryGetValue(ddsTopic, out var readers)
                ? readers.ToArray()
                : Array.Empty<LocalUserReader>();
        }
        foreach (var localReader in localReaders)
        {
            MatchLocalReaderWithLocalWriter(localReader, localWriter);
        }
        foreach (var remoteReader in _discoveryDb.ReaderSnapshot())
        {
            if (remoteReader.TopicName == ddsTopic)
            {
                MatchLocalWriterWithRemoteReader(localWriter, remoteReader);
            }
        }
        _ = RunSedpOperationAsync(
            token => _sedpPublicationsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local writer endpoint");

        var pub = new Publisher<T>(topicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
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
        SynchronizationContext? handlerContext = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var ddsTypeName = ResolveDdsTypeName<T>(typeName);
        var readerEntityId = _userEntityIds.AllocateReader();
        var reader = new StatelessReader(readerEntityId, _options.Logger, _options.DataFragReassembly);

        // SEDP 用に登録 (即時 publish)
        var endpointGuid = new Guid(GuidPrefix, readerEntityId);
        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = endpointGuid,
            ParticipantGuid = Guid,
            TopicName = ddsTopic,
            TypeName = ddsTypeName,
            Reliability = ReliabilityQos.BestEffort,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(_transports.DefaultUnicastLocator);
        endpointData.MulticastLocators.Add(_transports.UserMulticastDestination);
        var localReader = new LocalUserReader(endpointData, reader);
        LocalUserWriter[] localWriters;
        lock (_localEndpointsLock)
        {
            _localReaders.Add(endpointData);
            if (!_localUserReaders.TryGetValue(ddsTopic, out var readers))
            {
                readers = new List<LocalUserReader>();
                _localUserReaders[ddsTopic] = readers;
            }
            readers.Add(localReader);
            RefreshLocalUserEndpointSnapshotsLocked();

            localWriters = _localUserWriters.TryGetValue(ddsTopic, out var writers)
                ? writers.ToArray()
                : Array.Empty<LocalUserWriter>();
        }
        foreach (var localWriter in localWriters)
        {
            MatchLocalReaderWithLocalWriter(localReader, localWriter);
        }
        foreach (var remoteWriter in _discoveryDb.WriterSnapshot())
        {
            if (remoteWriter.TopicName == ddsTopic)
            {
                MatchLocalReaderWithRemoteWriter(localReader, remoteWriter);
            }
        }
        _ = RunSedpOperationAsync(
            token => _sedpSubscriptionsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local reader endpoint");

        return new Subscription<T>(
            topicName,
            endpointGuid,
            reader,
            serializer,
            handler,
            UnregisterLocalReader,
            handlerContext,
            _options.Logger,
            cdrReadLimits: _options.CdrReadLimits);
    }

    /// <summary>ハンドラが GuidPrefix を必要としない場合のショートカット。</summary>
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null)
        => CreateSubscription<T>(topicName, serializer, (value, _) => handler(value), handlerContext: handlerContext);

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
            LocalUserWriter[] writers;
            LocalUserReader[] readers;
            lock (_localEndpointsLock)
            {
                writers = _localUserWriters.Values.SelectMany(static list => list).ToArray();
                readers = _localUserReaders.Values.SelectMany(static list => list).ToArray();
            }
            foreach (var local in writers)
            {
                UnregisterLocalWriter(local.EndpointData.EndpointGuid, local.Writer);
                local.Writer.Dispose();
            }
            foreach (var local in readers)
            {
                UnregisterLocalReader(local.EndpointData.EndpointGuid, local.Reader);
                local.Reader.Dispose();
            }
        }
        finally
        {
            _unregisteringLocalEndpoints = false;
        }
    }

    private void UnregisterLocalWriter(Guid endpointGuid, StatefulWriter writerToRemove)
    {
        DiscoveredEndpointData? endpoint = null;
        bool hasRemainingEndpoint = false;
        LocalUserReader[] matchedLocalReaders = Array.Empty<LocalUserReader>();
        lock (_localEndpointsLock)
        {
            for (int i = 0; i < _localWriters.Count; i++)
            {
                if (!_localWriters[i].EndpointGuid.Equals(endpointGuid))
                {
                    continue;
                }
                endpoint = _localWriters[i];
                _localWriters.RemoveAt(i);
                break;
            }
            if (endpoint is not null
                && _localUserWriters.TryGetValue(endpoint.TopicName, out var writers))
            {
                for (int i = 0; i < writers.Count; i++)
                {
                    if (!ReferenceEquals(writers[i].Writer, writerToRemove))
                    {
                        continue;
                    }
                    writers.RemoveAt(i);
                    break;
                }
                hasRemainingEndpoint = writers.Any(w => w.EndpointData.EndpointGuid.Equals(endpointGuid));
                if (writers.Count == 0)
                {
                    _localUserWriters.Remove(endpoint.TopicName);
                }
                RefreshLocalUserEndpointSnapshotsLocked();
                matchedLocalReaders = _localUserReaders.TryGetValue(endpoint.TopicName, out var readers)
                    ? readers.ToArray()
                    : Array.Empty<LocalUserReader>();
            }
        }

        if (endpoint is null)
        {
            return;
        }
        foreach (var localReader in matchedLocalReaders)
        {
            localReader.Reader.UnmatchWriter(endpointGuid);
        }
        if (!hasRemainingEndpoint)
        {
            WaitForSedpUnregister(_sedpPublicationsWriter.UnregisterEndpointAsync(endpoint));
        }
    }

    private void UnregisterLocalReader(Guid endpointGuid, StatelessReader readerToRemove)
    {
        DiscoveredEndpointData? endpoint = null;
        bool hasRemainingEndpoint = false;
        LocalUserWriter[] matchedLocalWriters = Array.Empty<LocalUserWriter>();
        lock (_localEndpointsLock)
        {
            for (int i = 0; i < _localReaders.Count; i++)
            {
                if (!_localReaders[i].EndpointGuid.Equals(endpointGuid))
                {
                    continue;
                }
                endpoint = _localReaders[i];
                _localReaders.RemoveAt(i);
                break;
            }
            if (endpoint is not null
                && _localUserReaders.TryGetValue(endpoint.TopicName, out var readers))
            {
                for (int i = 0; i < readers.Count; i++)
                {
                    if (!ReferenceEquals(readers[i].Reader, readerToRemove))
                    {
                        continue;
                    }
                    readers.RemoveAt(i);
                    break;
                }
                hasRemainingEndpoint = readers.Any(r => r.EndpointData.EndpointGuid.Equals(endpointGuid));
                if (readers.Count == 0)
                {
                    _localUserReaders.Remove(endpoint.TopicName);
                }
                RefreshLocalUserEndpointSnapshotsLocked();
                matchedLocalWriters = _localUserWriters.TryGetValue(endpoint.TopicName, out var writers)
                    ? writers.ToArray()
                    : Array.Empty<LocalUserWriter>();
            }
        }

        if (endpoint is null)
        {
            return;
        }
        foreach (var localWriter in matchedLocalWriters)
        {
            localWriter.Writer.UnmatchReader(endpointGuid);
        }
        if (!hasRemainingEndpoint)
        {
            WaitForSedpUnregister(_sedpSubscriptionsWriter.UnregisterEndpointAsync(endpoint));
        }
    }

    private void WaitForSedpUnregister(ValueTask unregisterTask)
    {
        try
        {
            var task = unregisterTask.AsTask();
            if (!task.Wait(TimeSpan.FromMilliseconds(500)))
            {
                _options.Logger.Warn("DomainParticipant timed out while sending SEDP unregister");
            }
        }
        catch (AggregateException ex)
        {
            _options.Logger.Warn("DomainParticipant failed to send SEDP unregister", ex);
        }
        catch (Exception ex)
        {
            _options.Logger.Warn("DomainParticipant failed to send SEDP unregister", ex);
        }
    }

    private void RefreshLocalUserEndpointSnapshotsLocked()
    {
        _localUserWriterSnapshot = _localUserWriters.Values
            .SelectMany(static list => list)
            .Select(static local => local.Writer)
            .ToArray();
        _localUserReaderSnapshot = _localUserReaders.Values
            .SelectMany(static list => list)
            .Select(static local => local.Reader)
            .ToArray();
    }

    private async Task RunSedpOperationAsync(
        Func<CancellationToken, ValueTask> operation,
        string failureMessage)
    {
        var token = _leaseExpiryCts?.Token ?? CancellationToken.None;
        try
        {
            await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed || token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _options.Logger.Warn(failureMessage, ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    private static string ResolveDdsTypeName<T>(string? explicitTypeName)
    {
        if (!string.IsNullOrEmpty(explicitTypeName))
        {
            return explicitTypeName;
        }

        var field = typeof(T).GetField(
            "DdsTypeName",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        return field?.GetRawConstantValue() as string ?? "";
    }

    private static TimeSpan ComputeLeaseExpiryCheckPeriod(DomainParticipantOptions options)
    {
        var period = MinPositive(MaxLeaseExpiryCheckPeriod, options.SpdpInterval);
        var leaseDuration = options.LeaseDuration.ToTimeSpan();
        if (leaseDuration > TimeSpan.Zero)
        {
            var leaseQuarter = TimeSpan.FromTicks(Math.Max(1L, leaseDuration.Ticks / 4L));
            period = MinPositive(period, leaseQuarter);
        }
        return period < MinLeaseExpiryCheckPeriod ? MinLeaseExpiryCheckPeriod : period;
    }

    private static TimeSpan MinPositive(TimeSpan left, TimeSpan right)
    {
        if (left <= TimeSpan.Zero)
        {
            return right;
        }
        if (right <= TimeSpan.Zero)
        {
            return left;
        }
        return left <= right ? left : right;
    }
}
