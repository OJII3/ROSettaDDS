# Publisher Hot Path 続編: Payload 所有権 + Batch API

- 日付: 2026-06-22
- ステータス: ドラフト (ユーザーレビュー待ち)
- 親 spec: `docs/superpowers/specs/2026-06-22-publisher-hot-path-arraypool.md` (#86)
- 検証結果: `docs/superpowers/specs/2026-06-22-perf-followup-verification.md`
- ブランチ: `feat/publisher-owned-payload`

## 背景と目的

`docs/superpowers/specs/2026-06-22-publisher-hot-path-arraypool.md` (PR #86) で
`StatefulWriter` 経路は `ArrayPool<byte>.Shared` 経由になったが、`Publisher<T>.PublishAsync`
経路のホットスポットは `Publisher.SerializeWithEncapsulation` (`src/rosettadds/Dds/Publisher.cs:62-72`)
の `var buffer = new byte[totalCapacity];` が依然として残っている。

検証 (2026-06-22, `artifacts/perf/20260622-092922`) では
`unity-to-ros2-reliable-32` の mps は 6 892 (baseline) → 6 860 (新ラン) で 1.5x
改善目標 (10 000+) 未達。allocation rate も 4.4 MB / 2 506 samples ≈
**1.77 KB / frame ≈ 24 MB / sec** の GC 圧が続いている。

review サブエージェント協議 (2026-06-22) の結論:

- A 案 (1 個の burst buffer を最後に Return) は **reliable では不可**: history が
  slice を保持し続けるので use-after-return。slab 化しても A の派生 (B/D 相当)
  になる。
- B 案 (CacheChange が rented buffer を所有し evict / ACK / Dispose で pool へ return)
  が基盤として正しい。
- `PublishAsync` は ACK を await していない (`OnAckNack` / `PurgeAckedSamples` で
  別処理)。ボトルネックは socket send await と **Unity continuation 直列化**。
- batch API で 1 ループ内の per-message await を 1 個に減らすのが実効的。

## ゴール

`Publisher<T>.PublishAsync` 経路を **zero heap alloc / publish** 化し、
`unity-to-ros2-reliable-32` の mps を **10 000+** (1.5x) にする。

成功基準:

1. `Publisher.PublishAsync` 経路で `new byte[]` が 0 件 (既存テスト +
   `perf/unity-publisher-bottleneck` ブランチの allocation 計測で確認)
2. `unity-to-ros2-reliable-32` の mps ≥ 10 000 (1.5x)
3. reliable retransmit (`PurgeAckedSamples` → `_history.RemoveBelowOrEqual`) で
   pool へ正しく return される
4. 既存テスト (`StatefulHandshakeTests`, `ParticipantRtpsReceiverTests`,
   `PubSubLoopbackTests`, `SubscriptionCdrReadLimitsTests`,
   `ROSettaDDSUnityAotPlayerTests`, `ROSettaDDSUnityGeneratedMessageTests`,
   `ROSettaDDSUnityVerificationTests`) がすべて pass
5. `Publisher.SerializeWithEncapsulation(T value)` の public API は
   互換維持 (ReadOnlyMemory を返す版をそのまま残す)

## スコープ

**本スペック**:

- `RtpsPayloadOwner` 内部型の追加 (ArrayPool への返却責務)
- `CacheChange.PayloadOwner` 内部プロパティ追加
- `WriterHistoryCache` の `Add` overload 追加、`Remove` / `RemoveBelowOrEqual` /
  `EvictIfNeeded` で owner を dispose、`Dispose` 新設
- `StatefulWriter.WriteOwnedAsync` internal メソッド追加、`Dispose` で history を dispose
- `Publisher.PublishAsync` 経路の owned payload 化
- `Publisher.PublishManyAsync(IReadOnlyList<T>, CancellationToken)` 新設
- `Publisher.PublishRepeatedAsync(T, int, CancellationToken)` 新設
  (perf harness / hot loop 向け)
- `PerfPlayerEntry.RunUnityToRos2` の publish loop を `PublishRepeatedAsync`
  経由に置換

**本スペック外**:

- `StatelessWriter` (デッドコード、前 spec と同じ判断)
- `Heartbeat` / `Gap` 経路の pool 化 (hot path ではない)
- 8 KiB best-effort ROS 2→Unity timeout (別 issue)
- async/await 段数の削減 (`StatefulWriter` 内の ConfigureAwait 以外の構造変更)
- `Publisher.SerializeWithEncapsulation(T)` の public API 変更

## アーキテクチャ

### 1. `RtpsPayloadOwner` (新規 internal 型)

```csharp
namespace ROSettaDDS.Rtps.HistoryCache;

internal sealed class RtpsPayloadOwner : IDisposable
{
    private byte[]? _buffer;

    internal RtpsPayloadOwner(byte[] buffer) { _buffer = buffer; }
    internal byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(RtpsPayloadOwner));

    public void Dispose()
    {
        var b = System.Threading.Interlocked.Exchange(ref _buffer, null);
        if (b != null) System.Buffers.ArrayPool<byte>.Shared.Return(b);
    }
}
```

ポイント:

- `byte[]` を 1 個だけ所有する軽量 IDisposable
- `Dispose` は冪等 (`Interlocked.Exchange` で多重 dispose 安全)
- `internal` 公開。Publisher → StatefulWriter → WriterHistoryCache → CacheChange
  の内部経路でのみ使用

### 2. `CacheChange` 拡張

`src/rosettadds/Rtps/HistoryCache/CacheChange.cs` に `PayloadOwner` プロパティ
(内部) を追加。コンストラクタの optional 引数で受け取る。`PayloadOwner` が
`null` の場合は現在と同じく caller が lifetime に責任を持つ。

```csharp
public sealed class CacheChange
{
    public ChangeKind Kind { get; }
    public Guid WriterGuid { get; }
    public SequenceNumber SequenceNumber { get; }
    public Time SourceTimestamp { get; }
    public ReadOnlyMemory<byte> SerializedPayload { get; }
    public ReadOnlyMemory<byte> InlineQos { get; }
    public CdrEndianness InlineQosEndianness { get; }
    internal RtpsPayloadOwner? PayloadOwner { get; }  // ← 追加

    public CacheChange(
        ChangeKind kind,
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        Time sourceTimestamp,
        ReadOnlyMemory<byte> serializedPayload,
        ReadOnlyMemory<byte> inlineQos = default,
        CdrEndianness inlineQosEndianness = CdrEndianness.LittleEndian,
        RtpsPayloadOwner? payloadOwner = null)  // ← 追加
    { ... }
}
```

`internal` 公開なので外部テストからは触れない (既存テストは触らない)。

### 3. `WriterHistoryCache` 変更

- 既存 `Add(kind, payload, timestamp)` は owner なしで従来動作 (互換維持)
- 新規 overload: `Add(kind, payload, owner, timestamp)` で owner を受け取り
  CacheChange に伝搬
- `RemoveBelowOrEqual(SN)`, `Remove(SN)`, `EvictIfNeeded()` で該当
  CacheChange の `PayloadOwner` を dispose してから `_changes` から削除
- `Dispose()` 新設: 全 CacheChange の `PayloadOwner` を dispose し、自身を
  disposed 状態に

```csharp
public CacheChange Add(ChangeKind kind, ReadOnlyMemory<byte> payload, Time sourceTimestamp)
    => Add(kind, payload, payloadOwner: null, sourceTimestamp);

public CacheChange Add(ChangeKind kind, ReadOnlyMemory<byte> payload, RtpsPayloadOwner? payloadOwner, Time sourceTimestamp)
{
    lock (_lock)
    {
        _lastSequence++;
        var sn = new SequenceNumber(_lastSequence);
        var change = new CacheChange(kind, _writerGuid, sn, sourceTimestamp, payload,
            payloadOwner: payloadOwner);
        _changes[_lastSequence] = change;
        EvictIfNeeded();
        return change;
    }
}

public void RemoveBelowOrEqual(SequenceNumber sn)
{
    lock (_lock)
    {
        var keysToRemove = _changes.Keys.Where(k => k <= sn.Value).ToArray();
        foreach (var k in keysToRemove)
        {
            if (_changes.TryGetValue(k, out var change))
            {
                change.PayloadOwner?.Dispose();
            }
            _changes.Remove(k);
        }
    }
}

public bool Remove(SequenceNumber sn)
{
    lock (_lock)
    {
        if (_changes.TryGetValue(sn.Value, out var change))
        {
            change.PayloadOwner?.Dispose();
            _changes.Remove(sn.Value);
            return true;
        }
        return false;
    }
}

private void EvictIfNeeded()
{
    if (MaxSamples <= 0) return;
    while (_changes.Count > MaxSamples)
    {
        var firstKey = _changes.Keys.First();
        if (_changes.TryGetValue(firstKey, out var change))
        {
            change.PayloadOwner?.Dispose();
        }
        _changes.Remove(firstKey);
    }
}

private bool _disposed;
public void Dispose()
{
    lock (_lock)
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var change in _changes.Values)
        {
            change.PayloadOwner?.Dispose();
        }
        _changes.Clear();
    }
}
```

### 4. `StatefulWriter` 変更

- 既存 `WriteAsync(ReadOnlyMemory<byte>, ...)` は owner なしで互換維持
  (旧 `Add(kind, payload, timestamp)` 呼び出しに変更なし)
- 新規 `internal WriteOwnedAsync(RtpsPayloadOwner owner, ReadOnlyMemory<byte> payload, ...)`:
  `Add(kind, payload, owner, timestamp)` 経由で history に渡し、
  `SendDataAsync` を await
- 新規 `internal WriteBatchAsync(RtpsPayloadOwner[] owners, ReadOnlyMemory<byte>[] memories, CancellationToken)`:
  batch 用。1 回の async method 呼び出しで N 件分の `WriteOwnedAsync` を
  `ConfigureAwait(false)` 経由で await。main thread は batch 終了時の 1 回だけ
  resume
- **所有権の境界**: `WriteOwnedAsync` / `WriteBatchAsync` 内で `Add` が例外を
  投げた場合のみ `owner.Dispose()` を呼んで release する。`Add` 成功後は
  history が owner を所有し、evict / ACK / writer Dispose 経路で release する
- `StatefulWriter.Dispose` で `_history.Dispose()` を呼ぶ

```csharp
internal async ValueTask WriteOwnedAsync(
    RtpsPayloadOwner owner, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
{
    ThrowIfDisposed();
    CacheChange change;
    try
    {
        change = _history.Add(ChangeKind.Alive, payload, owner, Time.Now());
    }
    catch
    {
        // history.Add 失敗時: owner は publisher 責任のまま。
        // ここで dispose しないと buffer がリークする。
        owner.Dispose();
        throw;
    }
    // Add 成功後: owner は history 所有。SendDataAsync が失敗しても
    // history 側の evict / writer Dispose で release される。
    await SendDataAsync(change, ct).ConfigureAwait(false);
}

internal async ValueTask WriteBatchAsync(
    RtpsPayloadOwner[] owners, ReadOnlyMemory<byte>[] memories, CancellationToken ct = default)
{
    ThrowIfDisposed();
    int n = owners.Length;
    // 1. 全て history.Add (sync)。lock 競合は最小。
    var changes = new CacheChange[n];
    for (int i = 0; i < n; i++)
    {
        try
        {
            changes[i] = _history.Add(ChangeKind.Alive, memories[i], owners[i], Time.Now());
        }
        catch
        {
            owners[i].Dispose();
            // 既に Add 済の分は history 所有。残りはここで release しない (WriteOwnedAsync と同じ規約)。
            throw;
        }
    }
    // 2. 全 SendDataAsync を fire。ConfigureAwait(false) で continuation は ThreadPool。
    //    i=0 から順に追加された順に send (FIFO)。ACK 処理は別経路。
    for (int i = 0; i < n; i++)
    {
        await SendDataAsync(changes[i], ct).ConfigureAwait(false);
    }
}

public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    Stop();
    _history.Dispose();  // ← 追加 (writer 停止後に history も片付ける)
}
```

**Publisher 側のエラーハンドリングはシンプル**: `WriteOwnedAsync` /
`WriteBatchAsync` 内で `Add` 失敗時の `owner.Dispose()` を完結させるため、
Publisher は catch で owner を dispose しない (二重 dispose による
Use-After-Return を避ける)。`Add` 成功後の `SendDataAsync` 失敗で owner は
history に残ったままになるが、writer Dispose / history evict / ACK purge で
必ず回収される。

### 5. `Publisher<T>` 変更

- 既存 `SerializeWithEncapsulation(T value)` の public API はそのまま
  (ReadOnlyMemory を返し、`new byte[]` のまま — テスト / デバッグ用)
- private ヘルパ `SerializeOwned(T value)` を新設: pool rent → serialize →
  RtpsPayloadOwner + ReadOnlyMemory を返す
- `PublishAsync` を `SerializeOwned` + `WriteOwnedAsync` 経由に書き換え
- `PublishManyAsync(IReadOnlyList<T>, CancellationToken)` 新設
- `PublishRepeatedAsync(T, int, CancellationToken)` 新設

```csharp
private (RtpsPayloadOwner owner, ReadOnlyMemory<byte> memory) SerializeOwned(T value)
{
    int sizeEstimate = _serializer.GetSerializedSize(value);
    int totalCapacity = CdrEncapsulation.Size + sizeEstimate + 16;
    byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(totalCapacity);
    CdrEncapsulation.Write(buffer, CdrEncapsulation.CdrLittleEndian);
    var w = new CdrWriter(buffer, CdrEndianness.LittleEndian, cdrOrigin: CdrEncapsulation.Size);
    _serializer.Serialize(ref w, in value);
    int payloadLength = w.Position;
    var owner = new RtpsPayloadOwner(buffer);
    return (owner, buffer.AsMemory(0, payloadLength));
}

public async ValueTask PublishAsync(T value, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    var (owner, memory) = SerializeOwned(value);
    // WriteOwnedAsync が Add 失敗時の owner.Dispose() を完結させる。
    // ここでは catch しない (二重 dispose で Use-After-Return の恐れ)。
    await _writer.WriteOwnedAsync(owner, memory, cancellationToken).ConfigureAwait(false);
}

public async ValueTask PublishManyAsync(IReadOnlyList<T> values, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    ArgumentNullException.ThrowIfNull(values);
    if (values.Count == 0) return;

    int n = values.Count;
    // Hot path 想定で List<T> 等で渡される。per-batch 1 回だけ確保。
    // 将来 ArrayPool<RtpsPayloadOwner>.Shared.Rent に切替可能。
    var owners = new RtpsPayloadOwner[n];
    var memories = new ReadOnlyMemory<byte>[n];
    try
    {
        for (int i = 0; i < n; i++)
        {
            (owners[i], memories[i]) = SerializeOwned(values[i]);
        }
        // batch 全体を送信。所有権は WriteOwnedAsync 境界で完全に history 側へ移転。
        // main thread は batch 終了時の 1 回の await resume のみ。
        await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        // owner は WriteBatchAsync 内で所有権移転済み。Add 失敗分も
        // WriteBatchAsync 側で release 済み。Publisher は何もしない。
        // 例外経路で残った owner は writer.Dispose / history evict / ACK purge で回収。
        throw;
    }
}

public ValueTask PublishRepeatedAsync(T value, int count, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    if (count <= 0) return default;
    // 同じ値を count 回 publish する shortcut。
    // List<T> 確保を避けるため、List 化を省く overload として実装する
    // (将来 owner 配列を ArrayPool<>.Shared.Rent に切替可能)。
    return PublishRepeatedCoreAsync(value, count, cancellationToken);
}

private async ValueTask PublishRepeatedCoreAsync(T value, int count, CancellationToken cancellationToken)
{
    var owners = new RtpsPayloadOwner[count];
    var memories = new ReadOnlyMemory<byte>[count];
    for (int i = 0; i < count; i++)
    {
        (owners[i], memories[i]) = SerializeOwned(value);
    }
    await _writer.WriteBatchAsync(owners, memories, cancellationToken).ConfigureAwait(false);
}
```

ポイント:

- `PublishAsync` 経路は `new byte[]` が消える (ArrayPool 経由)。各 publish で
  残る allocation は `RtpsPayloadOwner` 1 個 (16〜24 B 程度) のみ。GC 圧は
  baseline に対し **大幅減** (RtpsPayloadOwner は short-lived、.NET GC の
  gen0 内で即回収)
- `PublishManyAsync` / `PublishRepeatedAsync` の `RtpsPayloadOwner[n]` /
  `ReadOnlyMemory<byte>[n]` 配列は batch あたり 1 回だけ確保 (per-publish ではない)
- 所有権の移動は `WriteBatchAsync` / `WriteOwnedAsync` 境界に集約。Publisher
  側は catch で dispose しない (二重 dispose / Use-After-Return 回避)。
  例外時の未 release owner は writer.Dispose / history evict / ACK purge で回収
- `_writer.WriteBatchAsync` 内部は各 `WriteOwnedAsync` を `ConfigureAwait(false)`
  経由で await する。main thread は batch 開始時の serialize + Add と batch
  終了時の 1 回の await resume のみ。loop 毎の UnitySynchronizationContext
  への marshal が消える
- `SerializeWithEncapsulation(T)` public API は維持 (テスト /
  `PerfPlayerEntry.cs:101` の `serializedBytes = ...` 計測用)

### 6. Perf harness 更新

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:104-107` の loop を
`PublishRepeatedAsync` 経由に置換:

```csharp
// 旧
for (int i = 0; i < args.Messages; i++)
{
    await publisher.PublishAsync(message);
}

// 新
await publisher.PublishRepeatedAsync(message, args.Messages);
```

### 7. IL2CPP / AOT 互換

- `CdrWriter` (ref struct) は `SerializeOwned` 内の **同期 helper** に閉じる。
  async method の await 越えに `ref struct` を持ち越さない。
- `RtpsPayloadOwner` は class (参照型) なので async 越えも安全
- generic constraint は変更なし。`Publisher<T>` 既存 API 互換

## テスト

### 単体テスト (`tests/rosettadds.Tests/Rtps/HistoryCache/`)

- `WriterHistoryCacheOwnedPayloadTests` (新規):
  - `Add(payload, owner, ts)` で owner が CacheChange に紐づく
  - `RemoveBelowOrEqual` で該当 owner が dispose される (spy で確認)
  - `EvictIfNeeded` (MaxSamples 越え) で古い owner が dispose
  - `Dispose` で全 owner が dispose

### 既存テスト

- `StatefulHandshakeTests`: pass 維持 (owner なしの旧 `WriteAsync` 経路)
- `ParticipantRtpsReceiverTests`: pass 維持
- `PubSubLoopbackTests`: pass 維持
- `SubscriptionCdrReadLimitsTests`: `SerializeWithEncapsulation` を使うが
  public API 互換のため pass
- Unity `ROSettaDDSUnity*Tests`: pass 維持
- `RosettaDDS.Tests/PerfRunner/PerfScenarioTests`: pass 維持

### Allocation 計測

- `perf/unity-publisher-bottleneck` ブランチの `publisher hot path の
  allocation / throughput 計測` (commit 432a30e, 4da5ad3, 6d20454) を
  cherry-pick して本流へマージ。`Publisher.PublishAsync` 経路で
  `GetTotalAllocatedBytes` 増分が 0 であることを確認

### perf 計測 (回帰確認)

`tools/rosettadds-perf-runner` を再実行し、`artifacts/perf/<新ラン ID>/` を
取得して:

- `unity-to-ros2-reliable-32` の mps ≥ 10 000
- `gc_allocated_in_frame_bytes_total` が baseline 比で大幅減
  (Publisher 経路 1 publish あたり ~1.7 KB → ~0 B)
- 既存 scenarios (1400 / 8000 / 32 KiB) で退行なし

## 段階的実装方針

1. `RtpsPayloadOwner` を追加
2. `CacheChange.PayloadOwner` を追加
3. `WriterHistoryCache` の `Add` overload + `Remove` / `RemoveBelowOrEqual` /
   `EvictIfNeeded` の owner dispose + `Dispose` 新設
4. `StatefulWriter.WriteOwnedAsync` + `WriteBatchAsync` 追加 + `Dispose` で
   history dispose
5. `Publisher.SerializeOwned` 追加 + `PublishAsync` を owned 化
6. `Publisher.PublishManyAsync` / `PublishRepeatedAsync` 追加
7. `PerfPlayerEntry` の loop を `PublishRepeatedAsync` に置換
8. 既存テスト全 pass 確認 (`dotnet test rosettadds.sln`)
9. `perf/unity-publisher-bottleneck` ブランチの allocation test を
   cherry-pick して計測
10. perf runner を再実行して mps / allocation rate を確認

各ステップは独立にコミットし、テストで fail したら即座に revert できる粒度にする。

## 互換性 / リスク

- **API 互換**: `Publisher.SerializeWithEncapsulation(T value)` の
  シグネチャ・戻り値は変更なし。`Publisher.PublishAsync` のシグネチャも
  変更なし (内部実装のみ変更)。新規 API (`PublishManyAsync` /
  `PublishRepeatedAsync`) は追加のみ。
- **Lifetime**: pool buffer の return 経路は 3 種類 (evict / ACK / writer dispose)
  すべてに `Owner.Dispose()` を入れる。所有権の漏れは memory retention を
  起こすので、ユニットテストで spy して検証する
- **AOT**: `RtpsPayloadOwner` は class なので問題なし。`CdrWriter` の ref struct
  は同期 helper (`SerializeOwned`) 内に閉じている
- **Cancellation**: `PublishAsync` 経路で `_writer.WriteOwnedAsync` が
  `OperationCanceledException` を投げた場合、owner を `catch` で release
  してから throw。history 側 (cancel 後に history に入った sample) は
  writer dispose で回収
- **既存テスト互換**: `SerializeWithEncapsulation(T value)` の
  `ReadOnlyMemory<byte>` 戻り値を変えないので、`SubscriberCdrReadLimitsTests`
  など `SerializeWithEncapsulation` を使うテストはそのまま pass
- **GC pressure**: `RtpsPayloadOwner` 1 個 / publish の allocation は残る
  (16〜24 B)。publisher hot path 全体では 24 MB/sec → 数百 KB/sec 程度に減少
  する見込み
- **計測バイアス**: ArrayPool 化は GC 圧を下げるが、初回実行時は pool が
  拡張される分 warmup が長くなる。perf 計測は既存のとおり `measure_start` を
  match 後に取るので影響なし

## 将来の改善余地 (本スペック外)

- `RtpsPayloadOwner[n]` 配列を `ArrayPool<RtpsPayloadOwner>.Shared` 経由に
  (per-batch allocation も削減)
- `Publisher.PublishAsync` の RTPS packet 構築 (`StatefulWriter.BuildDataPacket`)
  の scratch buffer も owner に統合可能 (今回は 1 個に絞る)
- 8 KiB best-effort ROS 2→Unity timeout (別 issue)
- async/await 段数削減 (C 案)

## 参考リンク

- perf 計測 runner: `tools/rosettadds-perf-runner/`
- 直近計測: `artifacts/perf/20260622-092922/`
- 親 spec: `docs/superpowers/specs/2026-06-22-publisher-hot-path-arraypool.md`
- 検証 spec: `docs/superpowers/specs/2026-06-22-perf-followup-verification.md`
- 発見 spec: `docs/superpowers/specs/2026-06-22-perf-revisit-findings.md`
