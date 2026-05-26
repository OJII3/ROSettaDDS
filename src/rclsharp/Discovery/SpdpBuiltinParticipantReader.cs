using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Common.Logging;
using Rclsharp.Rtps;
using Rclsharp.Rtps.Submessages;
using Rclsharp.Transport;

namespace Rclsharp.Discovery;

/// <summary>
/// SPDP の builtin participant reader。
/// transport の <see cref="IRtpsTransport.Received"/> を購読し、
/// SPDP_BUILTIN_PARTICIPANT_WRITER から送信された DATA submessage を解釈して
/// <see cref="DiscoveryDb"/> へ Upsert する。
/// </summary>
public sealed class SpdpBuiltinParticipantReader : IDisposable
{
    private readonly IRtpsTransport _transport;
    private readonly DiscoveryDb _discoveryDb;
    private readonly GuidPrefix _localPrefix;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _clock;
    private readonly DiscoveryLimits _limits;

    private bool _started;
    private bool _disposed;

    /// <summary>SPDP DATA を受信して解析できたとき発火。<see cref="DiscoveryDb"/> 更新前。</summary>
    public event Action<ParticipantData>? ParticipantDataReceived;

    public SpdpBuiltinParticipantReader(
        IRtpsTransport transport,
        DiscoveryDb discoveryDb,
        GuidPrefix localPrefix,
        ILogger? logger = null,
        Func<DateTime>? clock = null,
        DiscoveryLimits? limits = null)
    {
        _transport = transport;
        _discoveryDb = discoveryDb;
        _localPrefix = localPrefix;
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? (() => DateTime.UtcNow);
        _limits = limits ?? DiscoveryLimits.Default;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }
        _transport.Received += OnPacketReceived;
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _transport.Received -= OnPacketReceived;
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    private void OnPacketReceived(ReadOnlyMemory<byte> packet, Locator source)
    {
        try
        {
            ProcessPacketBorrowed(packet);
        }
        catch (Exception ex)
        {
            _logger.Warn($"SpdpReader failed to parse packet from {source}", ex);
        }
    }

    /// <summary>
    /// 受信パケットを RTPS メッセージとして解釈し、SPDP DATA を抽出して DB へ反映する。
    /// </summary>
    public void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if (!RtpsHeader.TryRead(packet, out _, out _, out _))
        {
            // RTPS ヘッダではない (他のプロトコル等)
            return;
        }

        var reader = new RtpsMessageReader(packet);
        while (reader.TryReadNext(out var hdr, out var body))
        {
            if (hdr.Kind != SubmessageKind.Data)
            {
                continue;
            }
            var data = DataSubmessage.ReadBody(body, hdr.Endianness, hdr.Flags);
            if (!data.WriterEntityId.Equals(BuiltinEntityIds.SpdpBuiltinParticipantWriter))
            {
                continue;
            }
            if (data.SerializedPayload.IsEmpty)
            {
                continue;
            }
            HandleSpdpData(data.SerializedPayload.Span);
        }
    }

    private void ProcessPacketBorrowed(ReadOnlyMemory<byte> packet)
    {
        if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _))
        {
            return;
        }

        var reader = RtpsMessageReader.FromMemory(packet);
        while (reader.TryReadNextMemory(out var hdr, out var body))
        {
            if (hdr.Kind != SubmessageKind.Data)
            {
                continue;
            }
            var data = DataSubmessage.ReadBodyBorrowed(body, hdr.Endianness, hdr.Flags);
            if (!data.WriterEntityId.Equals(BuiltinEntityIds.SpdpBuiltinParticipantWriter))
            {
                continue;
            }
            if (data.SerializedPayload.IsEmpty)
            {
                continue;
            }
            HandleSpdpData(data.SerializedPayload.Span);
        }
    }

    private void HandleSpdpData(ReadOnlySpan<byte> serializedPayload)
    {
        if (serializedPayload.Length < CdrEncapsulation.Size)
        {
            _logger.Debug("SpdpReader: payload too small for encapsulation header");
            return;
        }
        var (kind, _) = CdrEncapsulation.Read(serializedPayload[..CdrEncapsulation.Size]);
        if (!CdrEncapsulation.IsParameterList(kind))
        {
            _logger.Debug($"SpdpReader: unexpected encapsulation kind 0x{kind:X4} (expected PL_CDR)");
            return;
        }
        var endian = CdrEncapsulation.GetEndianness(kind);
        var cdrReader = new CdrReader(serializedPayload, endian, cdrOrigin: CdrEncapsulation.Size);
        var participantData = ParticipantDataSerializer.Read(ref cdrReader, _limits);

        ParticipantDataReceived?.Invoke(participantData);
        _discoveryDb.UpsertParticipant(participantData, _clock(), ignorePrefix: _localPrefix);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
