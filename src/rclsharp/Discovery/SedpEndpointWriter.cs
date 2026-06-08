using System.Buffers;
using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps.HistoryCache;
using Rclsharp.Rtps.Writer;
using Rclsharp.Transport;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Discovery;

/// <summary>
/// SEDP の Publications または Subscriptions Writer。Reliable Stateful Writer ベース。
/// 各 endpoint = 1 サンプルとして history に追加し、新規 reader が match されたら NACK で再送される
/// (TRANSIENT_LOCAL に近い挙動)。
/// 同一 endpoint GUID の履歴は最新状態だけを保持し、古い alive/unregister は再送対象から外す。
/// </summary>
public sealed class SedpEndpointWriter : IDisposable
{
    private readonly StatefulWriter _stateful;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<Guid, SequenceNumber> _latestEndpointSequences = new();
    private bool _disposed;

    /// <summary>内部の StatefulWriter (matching 等の操作用)。</summary>
    public StatefulWriter Stateful => _stateful;

    public Guid Guid => _stateful.Guid;
    public EntityId WriterEntityId => _stateful.WriterEntityId;

    public SedpEndpointWriter(
        IRtpsTransport transport,
        Locator multicastDestination,
        ProtocolVersion version,
        VendorId vendorId,
        GuidPrefix localPrefix,
        EntityId writerEntityId,
        TimeSpan heartbeatPeriod,
        ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        var guid = new Guid(localPrefix, writerEntityId);
        var history = new WriterHistoryCache(guid);
        _stateful = new StatefulWriter(
            sendTransport: transport,
            multicastDestination: multicastDestination,
            version: version,
            vendorId: vendorId,
            localPrefix: localPrefix,
            writerEntityId: writerEntityId,
            heartbeatPeriod: heartbeatPeriod,
            history: history,
            logger: _logger,
            purgeAckedSamples: false,
            resendHistoryOnMatch: true);
    }

    public void Start() => _stateful.Start();
    public void Stop() => _stateful.Stop();

    /// <summary>remote SEDP Reader を match (この writer から DATA/HB が届くようにする)。</summary>
    public void MatchRemoteReader(Guid remoteSedpReaderGuid, Locator? unicastLocator = null)
    {
        ThrowIfDisposed();
        _stateful.MatchReader(remoteSedpReaderGuid, unicastLocator);
    }

    public void UnmatchRemoteReader(Guid remoteSedpReaderGuid) => _stateful.UnmatchReader(remoteSedpReaderGuid);

    /// <summary>
    /// 1 endpoint を SEDP に publish する (history に永続化)。
    /// 既に match されている remote reader にはすぐ DATA が送られる。
    /// 後から match される reader は HEARTBEAT→ACKNACK で再送を要求できる。
    /// </summary>
    public async ValueTask AddEndpointAsync(DiscoveredEndpointData endpoint, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = SerializeEndpoint(endpoint);
        await WriteLatestEndpointStateAsync(
            endpoint.EndpointGuid,
            payload,
            ChangeKind.Alive,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnregisterEndpointAsync(
        DiscoveredEndpointData endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = SerializeEndpointKey(endpoint);
        await WriteLatestEndpointStateAsync(
            endpoint.EndpointGuid,
            payload,
            ChangeKind.NotAliveUnregistered,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>WriterHistoryCache 上の累積件数 (テスト/デバッグ用)。</summary>
    public int PublishedCount => (int)_stateful.History.LastSequenceNumber.Value;

    /// <summary>ACKNACK 受信ハンドラ。transport.Received を購読してこれを呼ぶ。</summary>
    public void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
        => _stateful.OnPacketReceived(packet, source);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateful.Dispose();
        _writeGate.Dispose();
    }

    private async ValueTask WriteLatestEndpointStateAsync(
        Guid endpointGuid,
        ReadOnlyMemory<byte> payload,
        ChangeKind kind,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _stateful.WriteAsync(payload, kind, cancellationToken).ConfigureAwait(false);
            var latestSequence = _stateful.History.LastSequenceNumber;
            if (_latestEndpointSequences.TryGetValue(endpointGuid, out var previousSequence))
            {
                _stateful.History.Remove(previousSequence);
            }
            _latestEndpointSequences[endpointGuid] = latestSequence;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>encap PL_CDR_LE 4B + DiscoveredEndpointData を 1 つの byte[] にする。</summary>
    private static byte[] SerializeEndpoint(DiscoveredEndpointData endpoint)
    {
        var buf = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            CdrEncapsulation.Write(buf, CdrEncapsulation.PlCdrLittleEndian);
            var inner = new CdrWriter(buf, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
            DiscoveredEndpointDataSerializer.Write(ref inner, endpoint);
            int length = inner.Position;
            var copy = new byte[length];
            Buffer.BlockCopy(buf, 0, copy, 0, length);
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static byte[] SerializeEndpointKey(DiscoveredEndpointData endpoint)
    {
        var buf = ArrayPool<byte>.Shared.Rent(128);
        try
        {
            CdrEncapsulation.Write(buf, CdrEncapsulation.PlCdrLittleEndian);
            var inner = new CdrWriter(buf, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
            DiscoveredEndpointDataSerializer.WriteKey(ref inner, endpoint);
            int length = inner.Position;
            var copy = new byte[length];
            Buffer.BlockCopy(buf, 0, copy, 0, length);
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
