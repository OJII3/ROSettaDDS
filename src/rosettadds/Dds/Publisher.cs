using System.Buffers;
using System.Collections.Generic;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Rtps.HistoryCache;
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

    /// <summary>Publication マッチ状態 (Fast DDS 互換)。</summary>
    public PublicationMatchedStatus PublicationMatchedStatus => _writer.PublicationMatchedStatus;

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
        var (owner, memory) = SerializeOwned(value);
        // WriteOwnedAsync 内で Add 失敗時のみ owner は release 済み。
        // ここでは catch しない (二重 dispose / Use-After-Return 防止)。
        await _writer.WriteOwnedAsync(owner, memory, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 複数の値を一括送信する batch API。所有権は WriteBatchAsync 境界に集約。
    /// </summary>
    public async ValueTask PublishManyAsync(IReadOnlyList<T> values, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (values.Count == 0) return;

        int n = values.Count;
        var owners = new RtpsPayloadOwner[n];
        var memories = new ReadOnlyMemory<byte>[n];
        int created = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                (owners[i], memories[i]) = SerializeOwned(values[i]);
                created = i + 1;
            }
        }
        catch
        {
            for (int j = 0; j < created; j++)
                owners[j].Dispose();
            throw;
        }
        await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同じ値を <paramref name="count"/> 回連続送信する shortcut。
    /// </summary>
    public ValueTask PublishRepeatedAsync(T value, int count, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (count <= 0) return default;

        return PublishRepeatedCoreAsync(value, count, cancellationToken);
    }

    private async ValueTask PublishRepeatedCoreAsync(T value, int count, CancellationToken cancellationToken)
    {
        var owners = new RtpsPayloadOwner[count];
        var memories = new ReadOnlyMemory<byte>[count];
        int created = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                (owners[i], memories[i]) = SerializeOwned(value);
                created = i + 1;
            }
        }
        catch
        {
            for (int j = 0; j < created; j++)
                owners[j].Dispose();
            throw;
        }
        await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// ArrayPool から rent した buffer に serialize し、所有者情報 (RtpsPayloadOwner) と
    /// ペイロード (ReadOnlyMemory) を返す。所有者は history に移転し、
    /// history.Add 失敗時のみ呼び出し側で release する。
    /// </summary>
    private (RtpsPayloadOwner owner, ReadOnlyMemory<byte> memory) SerializeOwned(T value)
    {
        int sizeEstimate = _serializer.GetSerializedSize(value);
        int totalCapacity = CdrEncapsulation.Size + sizeEstimate + 16;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalCapacity);
        try
        {
            CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
            var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
            _serializer.Serialize(ref w, in value);
            int payloadLength = w.Position;
            var owner = new RtpsPayloadOwner(buffer);
            return (owner, buffer.AsMemory(0, payloadLength));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// matched reader 数が <paramref name="minCount"/> に達するまで待機する。
    /// 戻り値: true=達成 / false=タイムアウト。
    /// </summary>
    public Task<bool> WaitForMatchedAsync(
        int minCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return MatchWaiter.WaitUntilMatchedAsync(
            () => _writer.MatchedReaderCount,
            minCount, timeout, cancellationToken);
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
