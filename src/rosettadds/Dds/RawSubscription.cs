using ROSettaDDS.Common;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

public sealed class RawSubscription : IDisposable
{
    private readonly IUserReader _reader;
    private readonly Action<ReadOnlyMemory<byte>, GuidPrefix> _callback;
    private readonly Action<Guid, IUserReader>? _unregisterEndpoint;
    private bool _disposed;

    public string TopicName { get; }
    public Guid Guid { get; }

    internal RawSubscription(
        string topicName,
        Guid guid,
        IUserReader reader,
        Action<ReadOnlyMemory<byte>, GuidPrefix> callback,
        Action<Guid, IUserReader>? unregisterEndpoint = null,
        bool autoStart = true)
    {
        TopicName = topicName ?? throw new ArgumentNullException(nameof(topicName));
        Guid = guid;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _unregisterEndpoint = unregisterEndpoint;
        _reader.PayloadReceived += OnPayloadReceived;
        if (autoStart)
        {
            _reader.Start();
        }
    }

    public EntityId ReaderEntityId => _reader.ReaderEntityId;

    public int MatchedWriterCount => _reader.MatchedWriterCount;

    private void OnPayloadReceived(ReadOnlyMemory<byte> payload, GuidPrefix sourcePrefix)
    {
        if (_disposed)
        {
            return;
        }
        _callback(payload, sourcePrefix);
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
}
