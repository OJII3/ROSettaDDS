using System.Net;
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
/// Phase 6 で SEDP (Best-Effort) を追加。Reliable 化は Phase 7。
/// </summary>
public sealed class DomainParticipant : IDisposable
{
    private readonly DomainParticipantOptions _options;
    private readonly IRtpsTransport _multicastTransport;
    private readonly IRtpsTransport _unicastTransport;
    private readonly IRtpsTransport _userMulticastTransport;
    private readonly IRtpsTransport _userUnicastTransport;
    private readonly bool _ownsMulticastTransport;
    private readonly bool _ownsUnicastTransport;
    private readonly bool _ownsUserMulticastTransport;
    private readonly bool _ownsUserUnicastTransport;
    private readonly DiscoveryDb _discoveryDb;
    private readonly SpdpBuiltinParticipantReader _spdpReader;
    private readonly SpdpBuiltinParticipantWriter _spdpWriter;
    private readonly SedpEndpointWriter _sedpPublicationsWriter;
    private readonly SedpEndpointReader _sedpPublicationsReader;
    private readonly SedpEndpointWriter _sedpSubscriptionsWriter;
    private readonly SedpEndpointReader _sedpSubscriptionsReader;
    private readonly Locator _multicastDestination;
    private readonly Locator _userMulticastDestination;
    private readonly Locator _metatrafficUnicastLocator;
    private readonly Locator _metatrafficMulticastLocator;
    private readonly Locator _defaultMulticastLocator;
    private readonly Locator _defaultUnicastLocator;

    // ローカル endpoint 一覧 (SEDP 送信時に使用)
    private readonly object _localEndpointsLock = new();
    private readonly List<DiscoveredEndpointData> _localWriters = new();
    private readonly List<DiscoveredEndpointData> _localReaders = new();

    // ローカル StatefulWriter をトピック名でルックアップ (remote reader マッチング用)
    private readonly Dictionary<string, List<LocalUserWriter>> _localUserWriters = new();

    // ローカル StatelessReader をトピック名でルックアップ (remote writer マッチング用)
    private readonly Dictionary<string, List<LocalUserReader>> _localUserReaders = new();

    private bool _started;
    private bool _disposed;
    private bool _unregisteringLocalEndpoints;

    public DomainParticipantOptions Options => _options;
    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public DiscoveryDb DiscoveryDb => _discoveryDb;

    /// <summary>ユーザートピックの multicast 送受信に使うトランスポート (Phase 5)。</summary>
    public IRtpsTransport UserMulticastTransport => _userMulticastTransport;

    /// <summary>ユーザートピックの unicast 送受信に使うトランスポート。</summary>
    public IRtpsTransport UserUnicastTransport => _userUnicastTransport;

    /// <summary>ユーザートピックの multicast 送信先 Locator (Phase 5)。</summary>
    public Locator UserMulticastDestination => _userMulticastDestination;

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

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);

        int discoveryMulticastPort = RtpsPorts.DiscoveryMulticast(_options.DomainId);
        int discoveryUnicastPort = RtpsPorts.DiscoveryUnicast(_options.DomainId, _options.ParticipantId);
        int userMulticastPort = RtpsPorts.UserMulticast(_options.DomainId);
        int userUnicastPort = RtpsPorts.UserUnicast(_options.DomainId, _options.ParticipantId);

        // Multicast transport (受信用に bind、送信先 Locator も同じ)
        if (_options.CustomMulticastTransport is not null)
        {
            _multicastTransport = _options.CustomMulticastTransport;
            _ownsMulticastTransport = false;
        }
        else
        {
            _multicastTransport = UdpTransport.CreateMulticast(
                _options.MulticastGroup,
                discoveryMulticastPort,
                _options.MulticastInterface,
                _options.Logger);
            _ownsMulticastTransport = true;
        }

        // Unicast transport (Metatraffic 受信用)
        if (_options.CustomUnicastTransport is not null)
        {
            _unicastTransport = _options.CustomUnicastTransport;
            _ownsUnicastTransport = false;
        }
        else
        {
            var localAddr = _options.LocalUnicastAddress ?? IPAddress.Loopback;
            _unicastTransport = UdpTransport.CreateUnicast(
                localAddr,
                discoveryUnicastPort,
                _options.Logger);
            _ownsUnicastTransport = true;
        }

        // ユーザートピック用 Multicast transport (port = 7401 + 250*domain)
        if (_options.CustomUserMulticastTransport is not null)
        {
            _userMulticastTransport = _options.CustomUserMulticastTransport;
            _ownsUserMulticastTransport = false;
        }
        else
        {
            _userMulticastTransport = UdpTransport.CreateMulticast(
                _options.MulticastGroup,
                userMulticastPort,
                _options.MulticastInterface,
                _options.Logger);
            _ownsUserMulticastTransport = true;
        }

        // ユーザートピック用 Unicast transport (port = 7411 + 250*domain + 2*participant)
        if (_options.CustomUserUnicastTransport is not null)
        {
            _userUnicastTransport = _options.CustomUserUnicastTransport;
            _ownsUserUnicastTransport = false;
        }
        else
        {
            var localAddr = _options.LocalUnicastAddress ?? IPAddress.Loopback;
            _userUnicastTransport = UdpTransport.CreateUnicast(
                localAddr,
                userUnicastPort,
                _options.Logger);
            _ownsUserUnicastTransport = true;
        }

        _multicastDestination = Locator.FromUdpV4(_options.MulticastGroup, (uint)discoveryMulticastPort);
        _userMulticastDestination = Locator.FromUdpV4(_options.MulticastGroup, (uint)userMulticastPort);
        _metatrafficMulticastLocator = _multicastDestination;
        _metatrafficUnicastLocator = _unicastTransport.LocalLocator;
        _defaultMulticastLocator = _userMulticastDestination;
        _defaultUnicastLocator = _userUnicastTransport.LocalLocator;

        _discoveryDb = new DiscoveryDb();

        _spdpReader = new SpdpBuiltinParticipantReader(
            _multicastTransport, _discoveryDb, GuidPrefix, _options.Logger);

        _spdpWriter = new SpdpBuiltinParticipantWriter(
            transport: _multicastTransport,
            multicastDestination: _multicastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            participantDataProvider: BuildParticipantData,
            interval: _options.SpdpInterval,
            logger: _options.Logger);

        // SEDP (Phase 8 で Reliable 化)。
        // - Writer: multicast transport で送信、unicast transport で ACKNACK を受信
        // - Reader: multicast / unicast 両方で DATA/HB を受信、unicast で ACKNACK を返信
        _sedpPublicationsWriter = new SedpEndpointWriter(
            transport: _multicastTransport,
            multicastDestination: _multicastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpPublicationsReader = new SedpEndpointReader(
            replyTransport: _unicastTransport,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsReader,
            ackNackFallbackDestination: _multicastDestination,
            producedEndpointKind: EndpointKind.Writer,
            logger: _options.Logger);

        _sedpSubscriptionsWriter = new SedpEndpointWriter(
            transport: _multicastTransport,
            multicastDestination: _multicastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpSubscriptionsReader = new SedpEndpointReader(
            replyTransport: _unicastTransport,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsReader,
            ackNackFallbackDestination: _multicastDestination,
            producedEndpointKind: EndpointKind.Reader,
            logger: _options.Logger);

        // SPDP で remote participant を発見/更新したら SEDP endpoint を auto-match
        _discoveryDb.ParticipantDiscovered += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantUpdated += OnRemoteParticipantDiscovered;

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
            return;
        }

        local.Reader.MatchWriter(remoteWriter.Data.EndpointGuid);
        _options.Logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remoteWriter.TopicName} writer={remoteWriter.Data.EndpointGuid}");
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

    private void OnUserUnicastPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        // 全ローカル user writer に ACKNACK を渡す (entity ID で内部フィルタされる)
        StatefulWriter[] writers;
        StatelessReader[] readers;
        lock (_localEndpointsLock)
        {
            writers = _localUserWriters.Values.SelectMany(static list => list).Select(static w => w.Writer).ToArray();
            readers = _localUserReaders.Values.SelectMany(static list => list).Select(static r => r.Reader).ToArray();
        }
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
        => string.IsNullOrEmpty(localTypeName)
        || string.IsNullOrEmpty(remoteTypeName)
        || string.Equals(localTypeName, remoteTypeName, StringComparison.Ordinal);

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
        _multicastTransport.Start();
        _unicastTransport.Start();
        _userMulticastTransport.Start();
        _userUnicastTransport.Start();
        _spdpReader.Start();
        _spdpWriter.Start();

        // SEDP の DATA/HB は multicast (初期) と unicast (ACKNACK 返信先) の両方で受信する
        _multicastTransport.Received += _sedpPublicationsReader.OnPacketReceived;
        _multicastTransport.Received += _sedpSubscriptionsReader.OnPacketReceived;
        _unicastTransport.Received += _sedpPublicationsReader.OnPacketReceived;
        _unicastTransport.Received += _sedpSubscriptionsReader.OnPacketReceived;
        // SEDP writer は ACKNACK を unicast metatraffic で受ける
        _unicastTransport.Received += _sedpPublicationsWriter.OnPacketReceived;
        _unicastTransport.Received += _sedpSubscriptionsWriter.OnPacketReceived;

        // ユーザーデータ writer の ACKNACK をユニキャストで受け取る
        _userUnicastTransport.Received += OnUserUnicastPacketReceived;

        _sedpPublicationsWriter.Start();
        _sedpSubscriptionsWriter.Start();
        _started = true;
    }

    /// <summary>SPDP / SEDP / Transport を停止する。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _sedpPublicationsWriter.Stop();
        _sedpSubscriptionsWriter.Stop();
        _userUnicastTransport.Received -= OnUserUnicastPacketReceived;
        _multicastTransport.Received -= _sedpPublicationsReader.OnPacketReceived;
        _multicastTransport.Received -= _sedpSubscriptionsReader.OnPacketReceived;
        _unicastTransport.Received -= _sedpPublicationsReader.OnPacketReceived;
        _unicastTransport.Received -= _sedpSubscriptionsReader.OnPacketReceived;
        _unicastTransport.Received -= _sedpPublicationsWriter.OnPacketReceived;
        _unicastTransport.Received -= _sedpSubscriptionsWriter.OnPacketReceived;
        _spdpWriter.Stop();
        _spdpReader.Stop();
        _userUnicastTransport.Stop();
        _userMulticastTransport.Stop();
        _multicastTransport.Stop();
        _unicastTransport.Stop();
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
            BuiltinEndpoints = BuiltinEndpointSet.RclsharpDefault,
            LeaseDuration = _options.LeaseDuration,
            EntityName = _options.EntityName,
        };
        data.MetatrafficMulticastLocators.Add(_metatrafficMulticastLocator);
        data.MetatrafficUnicastLocators.Add(_metatrafficUnicastLocator);
        data.DefaultUnicastLocators.Add(_defaultUnicastLocator);
        data.DefaultMulticastLocators.Add(_defaultMulticastLocator);
        return data;
    }

    /// <summary>
    /// 指定トピックの Publisher を生成する。
    /// EntityId は <see cref="UserEntityIdAllocator"/> によりトピック名から決定論的に割り当てる
    /// (Phase 5 互換)。同時にローカル endpoint 一覧へ登録され、SEDP で広告される (Phase 6)。
    /// </summary>
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));

        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var ddsTypeName = ResolveDdsTypeName<T>(typeName);
        var writerEntityId = UserEntityIdAllocator.WriterFor(ddsTopic);
        var writerGuid = new Guid(GuidPrefix, writerEntityId);
        var history = new Rtps.HistoryCache.WriterHistoryCache(writerGuid, maxSamples: 1000);
        var writer = new StatefulWriter(
            sendTransport: _userUnicastTransport,
            multicastDestination: _userMulticastDestination,
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
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.Add(_defaultUnicastLocator);
        endpointData.MulticastLocators.Add(_defaultMulticastLocator);
        var localWriter = new LocalUserWriter(endpointData, writer);
        lock (_localEndpointsLock)
        {
            _localWriters.Add(endpointData);
            if (!_localUserWriters.TryGetValue(ddsTopic, out var writers))
            {
                writers = new List<LocalUserWriter>();
                _localUserWriters[ddsTopic] = writers;
            }
            writers.Add(localWriter);
        }
        foreach (var remoteReader in _discoveryDb.ReaderSnapshot())
        {
            if (remoteReader.TopicName == ddsTopic)
            {
                MatchLocalWriterWithRemoteReader(localWriter, remoteReader);
            }
        }
        _ = _sedpPublicationsWriter.AddEndpointAsync(endpointData);

        var pub = new Publisher<T>(topicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
    }

    /// <summary>
    /// 指定トピックの Subscription を生成する。
    /// 受信ループは即座に開始され、マッチする DATA を受信するとハンドラが呼ばれる。
    /// 同時にローカル endpoint 一覧へ登録され、SEDP で広告される (Phase 6)。
    /// </summary>
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var ddsTypeName = ResolveDdsTypeName<T>(typeName);
        var writerEntityId = UserEntityIdAllocator.WriterFor(ddsTopic);
        var readerEntityId = UserEntityIdAllocator.ReaderFor(ddsTopic);
        var reader = new StatelessReader(_userMulticastTransport, writerEntityId, _options.Logger);

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
        endpointData.UnicastLocators.Add(_defaultUnicastLocator);
        endpointData.MulticastLocators.Add(_defaultMulticastLocator);
        var localReader = new LocalUserReader(endpointData, reader);
        lock (_localEndpointsLock)
        {
            _localReaders.Add(endpointData);
            if (!_localUserReaders.TryGetValue(ddsTopic, out var readers))
            {
                readers = new List<LocalUserReader>();
                _localUserReaders[ddsTopic] = readers;
            }
            readers.Add(localReader);
        }
        foreach (var remoteWriter in _discoveryDb.WriterSnapshot())
        {
            if (remoteWriter.TopicName == ddsTopic && TypeMatches(endpointData.TypeName, remoteWriter.TypeName))
            {
                reader.MatchWriter(remoteWriter.Data.EndpointGuid);
            }
        }
        _ = _sedpSubscriptionsWriter.AddEndpointAsync(endpointData);

        return new Subscription<T>(topicName, endpointGuid, reader, serializer, handler, UnregisterLocalReader);
    }

    /// <summary>ハンドラが GuidPrefix を必要としない場合のショートカット。</summary>
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler)
        => CreateSubscription<T>(topicName, serializer, (value, _) => handler(value));

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
        if (_ownsUserUnicastTransport)
        {
            _userUnicastTransport.Dispose();
        }
        if (_ownsUserMulticastTransport)
        {
            _userMulticastTransport.Dispose();
        }
        if (_ownsMulticastTransport)
        {
            _multicastTransport.Dispose();
        }
        if (_ownsUnicastTransport)
        {
            _unicastTransport.Dispose();
        }
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
            }
        }

        if (endpoint is null)
        {
            return;
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
            }
        }

        if (endpoint is null)
        {
            return;
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
}
