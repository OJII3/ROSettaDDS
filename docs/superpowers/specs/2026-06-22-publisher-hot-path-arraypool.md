# Publisher Hot Path の ArrayPool 化による Unity→ROS 2 スループット改善

- 日付: 2026-06-22
- ステータス: ドラフト（ユーザーレビュー待ち）
- 対象: `artifacts/perf/20260621-034324` で観測された Unity→ROS 2 publisher の
  per-message 固定オーバーヘッド
- ブランチ: `perf/unity-publisher-bottleneck`

## 背景と目的

`tools/rosettadds-perf-runner` で取得した 2026-06-21 の metrics では
Unity→ROS 2 の publisher が payload 32 B と 1024 B でいずれも約 6,700 msg/s に
張り付き、同じ payload の ROS 2→Unity (約 11,000 msg/s) より約 1.6 倍遅い。
elapsed_ms も 73-75 ms でほぼ同じことから、メッセージ毎の固定オーバーヘッドが
ボトルネックであり、ペイロードサイズが律速ではないと推測される。

該当経路をコードで追跡すると、`Publisher<T>.PublishAsync` →
`StatefulWriter.WriteAsync` → `SendDataAsync` の中で `BuildDataPacket` が
publish ごとに次の 3 段を実行している (`src/rosettadds/Rtps/Writer/StatefulWriter.cs:571-593`)。

```csharp
var buffer = new byte[SendBufferSize];   // (1) 1500 B を必ず確保
var writer = new RtpsMessageWriter(buffer, _version, _vendorId, _localPrefix);
writer.WriteData(data);
var packet = new byte[writer.BytesWritten];  // (2) 送信サイズぶんの確保
writer.WrittenSpan.CopyTo(packet);           // (3) コピー
return packet;
```

`StatelessWriter.BuildPacket` (`src/rosettadds/Rtps/Writer/StatelessWriter.cs:76-92`) も
同型。受信側は既に `ArrayPool<byte>.Shared` を使っている
(`src/rosettadds/Transport/UdpTransport.cs:208,214,254`) ので、送信側だけ
整合していない。

## ゴール

`Publisher<T>.PublishAsync` (内部的には `StatefulWriter` / `StatelessWriter`) が
publish 1 回あたりに確保するヒープバッファを 0 件にし、Unity→ROS 2
スループットを少なくとも 1.5 倍以上に改善する。

成功基準:

1. `BuildDataPacket` / `BuildPacket` の各呼び出しで `new byte[]` を
   一切行わない (ベンチまたは MemoryProfiler で確認)
2. 既存テスト (`StatefulHandshakeTests`, `ParticipantRtpsReceiverTests`,
   `PubSubLoopbackTests`, `RunUnityToRos2` PlayMode 経路) がすべて pass
3. perf 再実行で `unity-to-ros2-reliable-32` の `messages_per_second` が
   6,700 → 10,000 以上 (約 1.5x)
4. パケットの lifetime は `Transport.SendAsync` 呼び出し中に限定し、
   await を越えて pool buffer を保持しない

## スコープ

- **本スペック**: `StatefulWriter.BuildDataPacket` /
  `StatefulWriter.BuildDataFragPackets` を `ArrayPool<byte>.Shared` 利用に
  書き換え。`IRtpsTransport.SendAsync` の `ReadOnlyMemory<byte>` lifetime
  規約を尊重
- **本スペック外**:
  - `StatelessWriter.BuildPacket` の ArrayPool 化 → `StatelessWriter` は
    現在 `Publisher<T>` API からもテストからも参照されていないデッドコード
    (`rg -n "new StatelessWriter" src/ tests/` の結果 0 件)。将来 `Publisher<T>`
    で Best-Effort 経路を有効化したときに再評価する
  - `Publisher.SerializeWithEncapsulation` の payload buffer pool 化
    (B 案 / 別 issue)
  - `HeartbeatPacket` / `GapPacket` の buffer 化 (hot path ではない)
  - best-effort 8 KiB ROS 2→Unity タイムアウト失敗の対処 (別 issue)
  - async/await 段数削減 (C 案 / 別 issue)

## アーキテクチャ

### 1. Packet builder シグネチャの変更

現状は `byte[] BuildDataPacket(...)` で `byte[]` を返している。
これを `ReadOnlyMemory<byte>` を返す builder に変える:

```csharp
// 旧
private byte[] BuildDataPacket(...);
private IReadOnlyList<byte[]> BuildDataPackets(...);
private ReadOnlyMemory<byte> BuildPacket(ReadOnlyMemory<byte>, long);  // 既に ReadOnlyMemory

// 新 (StatefulWriter)
private void BuildDataPacket(
    CacheChange change,
    EntityId readerEntityId,
    ReadOnlyMemory<byte> inlineQos,
    bool isAlive,
    Span<byte> destination,
    out int written);
```

`Span<byte>` を渡して書き込ませ、書き込んだ長さを `out` で返す方式に変える。
呼び出し側 (`SendDataToDestinationAsync`) が pool から scratch buffer を rent
し、builder に渡して書き込ませ、`Transport.SendAsync` に slice を渡してから
`finally` で `Return` する。

### 2. `StatefulWriter` 側実装

```csharp
private async ValueTask SendDataToDestinationAsync(
    CacheChange change, EntityId readerEntityId, Locator destination, CancellationToken cancellationToken)
{
    bool isAlive = change.Kind == ChangeKind.Alive;
    ReadOnlyMemory<byte> inlineQos = isAlive
        ? default
        : DataSubmessage.BuildStatusInfoInlineQos(ToStatusInfo(change.Kind), CdrEndianness.LittleEndian);

    int dataMessageSize = RtpsHeader.Size
        + SubmessageHeader.Size + Time.Size
        + SubmessageHeader.Size + DataSubmessage.FixedHeaderSize
        + inlineQos.Length
        + change.SerializedPayload.Length;

    byte[] scratch = ArrayPool<byte>.Shared.Rent(SendBufferSize);
    try
    {
        if (dataMessageSize <= SendBufferSize)
        {
            BuildDataPacket(change, readerEntityId, inlineQos, isAlive,
                scratch, out int written);
            await _transport.SendAsync(scratch.AsMemory(0, written), destination, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // fragmentation 経路は packet 数が複数なので、
            // まず packet 数を見積もって最大の fragment を 1 本ずつ送る
            BuildDataFragPacketsSequential(
                change, readerEntityId, inlineQos, isAlive,
                scratch, destination, cancellationToken).GetAwaiter().GetResult();
        }
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        _logger.Error("StatefulWriter DATA send failed", ex);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(scratch);
    }
}
```

ポイント:

- `SendDataAsync` (複数 reader への fan-out) は各 reader に対して
  `SendDataToDestinationAsync` を `await` する。scratch buffer は reader ごとに
  rent / return する (fanout 数 = 1 が現状の初期 scenario なので、大きな差は
  出ない)。
- `dataMessageSize > SendBufferSize` のときは fragment 送信が必要。fragment は
  packet ごとにサイズがほぼ固定 (`DataFragPayloadSize` 以下) なので、
  同じ scratch buffer を 1 本ずつ書き直して送る。fragment 数 1 で 8 KiB
  payload の場合は 8 本程度なので 1 scratch buffer で十分。
- `BuildDataPacket(..., Span<byte> dest, out int written)` は
  既存の `RtpsMessageWriter` を `scratch` に対して構築し、
  `writer.WrittenSpan.CopyTo(dest)` ではなく `dest` へ直接書き込む。

### 3. `StatelessWriter` 側実装

スコープ外 (「スコープ」セクション参照)。`StatelessWriter` はデッドコードの
ため、API 互換のためだけに既存実装を維持する。将来 `Publisher<T>` で
Best-Effort 経路を有効化したタイミングで同じパターン
(`Span<byte> destination, out int written` + `ArrayPool` 経由) を適用する。

### 4. Heartbeat / Gap 経路

`BuildHeartbeatPacket` / `BuildGapPacket` は hot path ではないが、同じ 2 段
alloc パターンを踏襲している。本スペックではスコープ外として明示的に触らない。
将来の改善余地としてスペック末尾の「将来の改善余地」に記載。

### 5. `IRtpsTransport.SendAsync` の lifetime 規約

`UdpTransport.cs:17-18` のコメント:

> 受信ハンドラ (`Received`) に渡す `ReadOnlyMemory<byte>` は呼び出し中のみ有効
> (`ArrayPool<T>` から借りたバッファを再利用する)。保持したい場合は呼び出し側で複製すること。

これと対称の規約を `SendAsync` 側にも明示する。`scratch.AsMemory(0, written)`
の Memory は `SendAsync` 呼び出し中 (= await を含む) だけ有効という不変条件を
呼び出し側 (`StatefulWriter` / `StatelessWriter`) が守る。`Return` は
`SendAsync` が完了したあと。

## テスト

### 単体テスト (`tests/rosettadds.Tests/Rtps/`)

新規: `WriterDataPacketTests.cs` (もしくは既存ファイルに追加)
- `BuildDataPacket` の出力内容が pool 利用前後で同一であること
  (比較対象を保持するために旧実装の wrapper を `InternalsVisibleTo` で一時
  公開しない。代わりに実送信した packet の sequence / length を確認する smoke
  テストに留める)
- `BuildPacket` (`StatelessWriter`) も同様

### 結合テスト

既存テストはすべて pass することを確認する:
- `StatefulHandshakeTests` (DATA / GAP / HB を含む RTPS 往復)
- `ParticipantRtpsReceiverTests` (DATA 受信)
- `PubSubLoopbackTests` (LoopbackHub 経由で publish/subscribe)

### perf 計測 (回帰確認)

`tools/rosettadds-perf-runner` を再実行し、metrics.ndjson から
`unity-to-ros2-reliable-32` の `messages_per_second` を比較する。
ベースライン値:

- reliable-32 Unity→ROS 2: 6,851 msg/s (73 ms / 500 msg)
- reliable-1024 Unity→ROS 2: 6,701 msg/s (75 ms / 500 msg)

成功基準: 10,000 msg/s 以上 (1.5x)。`serialized_bytes_per_message` が変わらない
こと、`elapsed_ms` が短縮されること、`helper` 側で `done.received` 件数 (= 500)
と一致することを合わせて確認する。

## 段階的実装方針

1. `StatefulWriter` の single-packet 経路を `ArrayPool` 化
   (fragmentation 経路は一旦従来実装のまま残し、送れない payload は現行 path)
2. `StatefulWriter` の fragmentation 経路を `ArrayPool` 化
3. `StatelessWriter.WriteAsync` を `ArrayPool` 化
   (旧 `BuildPacket(ReadOnlyMemory<byte>, long)` は overload として残す)
4. 既存テスト全 pass 確認 (`dotnet test rosettadds.sln`)
5. perf runner を 1 scenario (`unity-to-ros2-reliable-32`) で再実行し
   ベースラインと比較

各ステップは独立にコミットし、テストで fail したら即座に revert できる粒度にする。

## 互換性 / リスク

- **API 互換**: `BuildDataPacket` / `BuildDataPacket` のシグネチャは private
  なので呼び出し側影響なし。`StatelessWriter.BuildPacket` は `public` だが
  overload 追加なので呼び出し側影響なし。
- **Lifetime 規約**: pool buffer を await 中に保持しないことを
  `SendDataToDestinationAsync` 内で構造的に保証する (`SendAsync` 完了後に
  `Return`)。万一 `SendAsync` 実装側で Memory を backing storage より
  長く保持するケースがあればテストで検出する (smoke test を追加)。
- **Pool 競合**: `ArrayPool<byte>.Shared` はマルチスレッドで利用される前提で
  設計されている。`StatefulWriter` の fanout で複数 reader に同時送信する
  シナリオでは、各 reader への送信は sequential (`foreach` + `await`) なので
  1 度に複数 scratch buffer を rent することはない。fanout が将来並列化された
  場合は `ThreadStatic` バッファ等への切り替えを再検討する。
- **計測バイアス**: `ArrayPool` 化は GC 圧を下げるが、初回実行時は pool が
  拡張される分 warmup が長くなる。perf 計測は既存のとおり `measure_start` を
  match 後に取るので影響なし。
- **subscriber 経路**: 影響なし。`UdpTransport.ReceiveLoop` は既に pool
  利用。`Subscription<T>.OnPayloadReceived` 経路は別 hot path。

## 将来の改善余地 (本スペック外)

- `Publisher.SerializeWithEncapsulation` の payload buffer pool 化 (B 案)
- `Publisher<T>.PublishAsync` の async/await 段数削減 (C 案)
- `BuildHeartbeatPacket` / `BuildGapPacket` の pool 化
- 8 KiB best-effort ROS 2→Unity タイムアウト失敗 (別 issue)
- per-thread scratch buffer (`ThreadStatic<byte[]>`) による pool overhead 排除

## 参考リンク

- perf 計測 runner: `tools/rosettadds-perf-runner/`
- 直近計測: `artifacts/perf/20260621-034324/`
- 設計 spec: 本スペック
- 前段 spec: `docs/superpowers/specs/2026-06-21-unity-player-profiler-performance-design.md`
