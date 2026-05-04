using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.Reader;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Discovery;

/// <summary>
/// SEDP の Publications または Subscriptions Reader。
/// Phase 8 で Reliable Stateful Reader ベースに変更。
/// PayloadReceived → encap PL_CDR を解釈 → DiscoveryDb.UpsertEndpoint。
/// </summary>
public sealed class SedpEndpointReader : IDisposable
{
    private readonly StatefulReader _stateful;
    private readonly DiscoveryDb _discoveryDb;
    private readonly GuidPrefix _localPrefix;
    private readonly EndpointKind _producedEndpointKind;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _clock;
    private bool _disposed;

    public StatefulReader Stateful => _stateful;
    public Guid Guid => _stateful.Guid;
    public EntityId ReaderEntityId => _stateful.ReaderEntityId;

    /// <summary>parse 成功時に発火 (DiscoveryDb 更新前)。</summary>
    public event Action<DiscoveredEndpointData>? EndpointDataReceived;

    public SedpEndpointReader(
        IRtpsTransport replyTransport,
        DiscoveryDb discoveryDb,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId readerEntityId,
        Locator ackNackFallbackDestination,
        EndpointKind producedEndpointKind,
        ILogger? logger = null,
        Func<DateTime>? clock = null)
    {
        _discoveryDb = discoveryDb;
        _localPrefix = localPrefix;
        _producedEndpointKind = producedEndpointKind;
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? (() => DateTime.UtcNow);

        _stateful = new StatefulReader(
            replyTransport: replyTransport,
            version: version,
            vendorId: vendorId,
            localPrefix: localPrefix,
            readerEntityId: readerEntityId,
            ackNackFallbackDestination: ackNackFallbackDestination,
            logger: _logger);

        _stateful.PayloadReceived += OnPayloadReceived;
    }

    /// <summary>remote SEDP Writer を match (この reader が DATA を受け付けるようにする)。</summary>
    public void MatchRemoteWriter(Guid remoteSedpWriterGuid, Locator? unicastReplyLocator = null)
    {
        ThrowIfDisposed();
        _stateful.MatchWriter(remoteSedpWriterGuid, unicastReplyLocator);
    }

    public void UnmatchRemoteWriter(Guid remoteSedpWriterGuid) => _stateful.UnmatchWriter(remoteSedpWriterGuid);

    /// <summary>RTPS パケット受信ハンドラ。transport.Received を購読してこれを呼ぶ。</summary>
    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
        => _stateful.OnPacketReceived(packet, source);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateful.PayloadReceived -= OnPayloadReceived;
        _stateful.Dispose();
    }

    private void OnPayloadReceived(Rclsharp.Rtps.HistoryCache.CacheChange change)
    {
        try
        {
            var payload = change.SerializedPayload.Span;
            if (payload.Length < CdrEncapsulation.Size)
            {
                _logger.Debug("SedpEndpointReader: payload too small for encap header");
                return;
            }
            var (kind, _) = CdrEncapsulation.Read(payload[..CdrEncapsulation.Size]);
            if (!CdrEncapsulation.IsParameterList(kind))
            {
                _logger.Debug($"SedpEndpointReader: unexpected encapsulation 0x{kind:X4}");
                return;
            }
            var endian = CdrEncapsulation.GetEndianness(kind);
            var cdrReader = new CdrReader(payload, endian, cdrOrigin: CdrEncapsulation.Size);
            var endpointData = DiscoveredEndpointDataSerializer.Read(ref cdrReader, _producedEndpointKind);

            EndpointDataReceived?.Invoke(endpointData);
            if (change.Kind == Rclsharp.Rtps.HistoryCache.ChangeKind.Alive)
            {
                _discoveryDb.UpsertEndpoint(endpointData, _clock(), ignorePrefix: _localPrefix);
            }
            else
            {
                _discoveryDb.TryRemoveEndpoint(_producedEndpointKind, endpointData.EndpointGuid, _localPrefix);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("SedpEndpointReader failed to parse endpoint data", ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
