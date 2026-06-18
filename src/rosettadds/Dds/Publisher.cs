using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// 型付き Publisher。<see cref="ICdrSerializer{T}"/> でシリアライズして
/// <see cref="StatefulWriter"/> 経由で RELIABLE 配信する。
/// </summary>
public sealed class Publisher<T> : IDisposable
{
    private readonly StatefulWriter _writer;
    private readonly ICdrSerializer<T> _serializer;
    private readonly Action<Guid, StatefulWriter>? _unregisterEndpoint;
    private bool _disposed;

    public string TopicName { get; }
    public Guid Guid => _writer.Guid;
    internal StatefulWriter Writer => _writer;

    public Publisher(
        string topicName,
        StatefulWriter writer,
        ICdrSerializer<T> serializer,
        Action<Guid, StatefulWriter>? unregisterEndpoint = null)
    {
        TopicName = topicName;
        _writer = writer;
        _serializer = serializer;
        _unregisterEndpoint = unregisterEndpoint;
    }

    /// <summary>値をシリアライズ (encap header CDR_LE 付き) して 1 件送信する。</summary>
    public async ValueTask PublishAsync(T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = SerializeWithEncapsulation(value);
        await _writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>値を送信し、採番された RTPS シーケンス番号を返す (サービス用)。</summary>
    public ValueTask<SequenceNumber> PublishReturningSequenceNumberAsync(
        T value, CancellationToken cancellationToken = default)
        => PublishReturningSequenceNumberAsync(value, onSequenceAssigned: null, cancellationToken);

    /// <summary>値を送信し、SN 採番直後・送信前に <paramref name="onSequenceAssigned"/> を呼ぶ (サービス用)。</summary>
    public async ValueTask<SequenceNumber> PublishReturningSequenceNumberAsync(
        T value, Action<SequenceNumber>? onSequenceAssigned, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = SerializeWithEncapsulation(value);
        return await _writer.WriteReturningSequenceNumberAsync(payload, onSequenceAssigned, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>シリアライズ後のバイト列 (encap header 込み) を返す (テスト/デバッグ用)。</summary>
    public ReadOnlyMemory<byte> SerializeWithEncapsulation(T value)
    {
        int sizeEstimate = _serializer.GetSerializedSize(value);
        int totalCapacity = CdrEncapsulation.Size + sizeEstimate + 16;
        var buffer = new byte[totalCapacity];
        CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
        var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
        _serializer.Serialize(ref w, in value);
        int payloadLength = w.Position;
        return buffer.AsMemory(0, payloadLength);
    }

    public void Start() => _writer.Start();
    public void Stop() => _writer.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unregisterEndpoint?.Invoke(Guid, _writer);
        _writer.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
