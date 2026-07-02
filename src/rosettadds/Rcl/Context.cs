using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;
using ROSettaDDS.Transport;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_context_t 相当。ドメイン共通の DDS 資源を所有する。
/// 1 プロセス内で複数 Node をホストできる。
/// </summary>
public sealed class Context : IDisposable
{
    private readonly ContextOptions _options;
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

    private bool _started;
    private bool _disposed;

    public Context(ContextOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);

        _transports = ParticipantTransportSet.Create(_options);
        _receiver = new ParticipantRtpsReceiver(GuidPrefix, _options.Logger);

        _discoveryDb = new DiscoveryDb(_options.DiscoveryLimits);
        _leaseExpiryMonitor = new LeaseExpiryMonitor(_discoveryDb, _options, _options.Logger);

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

        // builtin endpoint を単一 receiver のルーティング対象として登録する。
        _receiver.RegisterReader(BuiltinEntityIds.SpdpBuiltinParticipantReader, _spdpReader);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinPublicationsReader, _sedpPublicationsReader.Stateful);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinSubscriptionsReader, _sedpSubscriptionsReader.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinPublicationsWriter, _sedpPublicationsWriter.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinSubscriptionsWriter, _sedpSubscriptionsWriter.Stateful);

        // SPDP で remote participant を発見/更新したら SEDP endpoint を auto-match
        _discoveryDb.ParticipantDiscovered += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantUpdated += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantLost += OnRemoteParticipantLost;
    }

    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public ContextOptions Options => _options;
    public ILogger Logger => _options.Logger;

    public int ResolvedParticipantId => _transports.ResolvedParticipantId;
    public IRtpsTransport UserMulticastTransport => _transports.UserMulticast;
    public IRtpsTransport UserUnicastTransport => _transports.UserUnicast;
    public Locator UserMulticastDestination => _transports.UserMulticastDestination;
    public DiscoveryDb DiscoveryDb => _discoveryDb;

    public void Start()
    {
        ThrowIfDisposed();
        if (_started) return;
        _transports.Start();

        // participant 単位の単一 receiver が全 transport の受信を 1 経路に集約する。
        _receiver.Subscribe(_transports.MetatrafficMulticast);
        _receiver.Subscribe(_transports.MetatrafficUnicast);
        _receiver.Subscribe(_transports.UserMulticast);
        _receiver.Subscribe(_transports.UserUnicast);

        _spdpWriter.Start();
        _sedpPublicationsWriter.Start();
        _sedpSubscriptionsWriter.Start();
        _leaseExpiryMonitor.Start();
        _started = true;
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (!_started) return;
        _leaseExpiryMonitor.Stop();
        _sedpPublicationsWriter.Stop();
        _sedpSubscriptionsWriter.Stop();
        _receiver.UnsubscribeAll();
        _spdpWriter.Stop();
        _transports.Stop();
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Stop() は _disposed をチェックするので、先に Stop() してから _disposed = true にする。
        Stop();
        _disposed = true;
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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
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

        _options.Logger.Debug($"Context: auto-matched SEDP endpoints for {participant.Guid}");
    }

    private void OnRemoteParticipantLost(RemoteParticipant participant)
    {
        var prefix = participant.GuidPrefix;
        _sedpPublicationsWriter.UnmatchRemoteReader(new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsReader));
        _sedpSubscriptionsWriter.UnmatchRemoteReader(new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsReader));
        _sedpPublicationsReader.UnmatchRemoteWriter(new Guid(prefix, BuiltinEntityIds.SedpBuiltinPublicationsWriter));
        _sedpSubscriptionsReader.UnmatchRemoteWriter(new Guid(prefix, BuiltinEntityIds.SedpBuiltinSubscriptionsWriter));

        _options.Logger.Debug($"Context: unmatched SEDP endpoints for lost participant {participant.Guid}");
    }

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
}
