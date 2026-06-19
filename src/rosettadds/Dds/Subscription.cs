using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// 型付き Subscription。<see cref="IUserReader"/> から SerializedPayload を受け取って
/// <see cref="ICdrSerializer{T}"/> でデシリアライズし、ハンドラへ渡す。
/// Best-Effort / Reliable のどちらの reader でも同じ経路で扱う。
/// </summary>
public sealed class Subscription<T> : IDisposable
{
    private readonly IUserReader _reader;
    private readonly ICdrSerializer<T> _serializer;
    private readonly Action<T, GuidPrefix> _handler;
    private readonly Action<Guid, IUserReader>? _unregisterEndpoint;
    private readonly SynchronizationContext? _handlerContext;
    private readonly ILogger _logger;
    private readonly CdrReadLimits _cdrReadLimits;
    private bool _disposed;

    public string TopicName { get; }
    public Guid Guid { get; }
    public EntityId ReaderEntityId => _reader.ReaderEntityId;

    /// <summary>Subscription マッチ状態 (Fast DDS 互換)。</summary>
    public SubscriptionMatchedStatus SubscriptionMatchedStatus => _reader.SubscriptionMatchedStatus;

    internal Subscription(
        string topicName,
        Guid guid,
        IUserReader reader,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        Action<Guid, IUserReader>? unregisterEndpoint = null,
        SynchronizationContext? handlerContext = null,
        ILogger? logger = null,
        bool autoStart = true,
        CdrReadLimits? cdrReadLimits = null)
    {
        TopicName = topicName;
        Guid = guid;
        _reader = reader;
        _serializer = serializer;
        _handler = handler;
        _unregisterEndpoint = unregisterEndpoint;
        _handlerContext = handlerContext;
        _logger = logger ?? NullLogger.Instance;
        _cdrReadLimits = cdrReadLimits ?? CdrReadLimits.Default;
        _reader.PayloadReceived += OnPayloadReceived;
        if (autoStart)
        {
            _reader.Start();
        }
    }

    private void OnPayloadReceived(ReadOnlyMemory<byte> payload, GuidPrefix sourcePrefix)
    {
        T value;
        try
        {
            value = DeserializeWithEncapsulation(payload.Span);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Subscription failed to deserialize payload on topic {TopicName}", ex);
            return;
        }

        if (_handlerContext is null)
        {
            InvokeHandler(value, sourcePrefix);
            return;
        }

        _handlerContext.Post(
            static state =>
            {
                var callback = (HandlerCallback)state!;
                callback.Subscription.InvokeHandler(callback.Value, callback.SourcePrefix);
            },
            new HandlerCallback(this, value, sourcePrefix));
    }

    private void InvokeHandler(T value, GuidPrefix sourcePrefix)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _handler(value, sourcePrefix);
        }
        catch (Exception ex)
        {
            _logger.Error($"Subscription handler failed on topic {TopicName}", ex);
        }
    }

    /// <summary>
    /// matched writer 数が <paramref name="minCount"/> に達するまで待機する。
    /// 戻り値: true=達成 / false=タイムアウト。
    /// </summary>
    public Task<bool> WaitForMatchedAsync(
        int minCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return MatchWaiter.WaitUntilMatchedAsync(
            () => _reader.MatchedWriterCount,
            minCount, timeout, cancellationToken);
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
        var r = new CdrReader(payload, endian, cdrOrigin: CdrEncapsulation.Size, limits: _cdrReadLimits);
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
        _unregisterEndpoint?.Invoke(Guid, _reader);
        _reader.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class HandlerCallback
    {
        public HandlerCallback(Subscription<T> subscription, T value, GuidPrefix sourcePrefix)
        {
            Subscription = subscription;
            Value = value;
            SourcePrefix = sourcePrefix;
        }

        public Subscription<T> Subscription { get; }
        public T Value { get; }
        public GuidPrefix SourcePrefix { get; }
    }
}
