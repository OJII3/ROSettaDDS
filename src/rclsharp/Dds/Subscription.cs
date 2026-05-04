using Rclsharp.Cdr;
using Rclsharp.Common;
using Rclsharp.Rtps.Reader;

using Guid = Rclsharp.Common.Guid;

namespace Rclsharp.Dds;

/// <summary>
/// 型付き Subscription。<see cref="StatelessReader"/> から SerializedPayload を受け取って
/// <see cref="ICdrSerializer{T}"/> でデシリアライズし、ハンドラへ渡す。Best-Effort 専用 (Phase 5)。
/// </summary>
public sealed class Subscription<T> : IDisposable
{
    private readonly StatelessReader _reader;
    private readonly ICdrSerializer<T> _serializer;
    private readonly Action<T, GuidPrefix> _handler;
    private readonly Action<Guid>? _unregisterEndpoint;
    private bool _disposed;

    public string TopicName { get; }
    public Guid Guid { get; }
    public EntityId WriterEntityId => _reader.WriterEntityId;

    public Subscription(
        string topicName,
        Guid guid,
        StatelessReader reader,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        Action<Guid>? unregisterEndpoint = null,
        bool autoStart = true)
    {
        TopicName = topicName;
        Guid = guid;
        _reader = reader;
        _serializer = serializer;
        _handler = handler;
        _unregisterEndpoint = unregisterEndpoint;
        _reader.PayloadReceived += OnPayloadReceived;
        if (autoStart)
        {
            _reader.Start();
        }
    }

    private void OnPayloadReceived(ReadOnlyMemory<byte> payload, GuidPrefix sourcePrefix)
    {
        try
        {
            T value = DeserializeWithEncapsulation(payload.Span);
            _handler(value, sourcePrefix);
        }
        catch (Exception)
        {
            // Phase 5 では握り潰し (logger を渡せるよう Phase 6+ で拡張)
        }
    }

    /// <summary>encap header を解釈してデシリアライズする (テスト/デバッグ用)。</summary>
    public T DeserializeWithEncapsulation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CdrEncapsulation.Size)
        {
            throw new InvalidDataException(
                $"Payload too small for CDR encapsulation header (got {payload.Length} bytes).");
        }
        var (kind, _) = CdrEncapsulation.Read(payload[..CdrEncapsulation.Size]);
        var endian = CdrEncapsulation.GetEndianness(kind);
        var r = new CdrReader(payload, endian, cdrOrigin: CdrEncapsulation.Size);
        _serializer.Deserialize(ref r, out var value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reader.PayloadReceived -= OnPayloadReceived;
        _unregisterEndpoint?.Invoke(Guid);
        _reader.Dispose();
    }
}
