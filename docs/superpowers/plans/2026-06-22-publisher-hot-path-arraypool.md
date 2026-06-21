# Publisher Hot Path ArrayPool 化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `StatefulWriter` の DATA 送信経路で publish ごとの `new byte[]` を排除し、Unity→ROS 2 のスループットを 1.5x 以上に改善する。`StatelessWriter` はデッドコードのため対象外。

**Architecture:** `StatefulWriter.BuildDataPacket` を `Span<byte> destination, out int written` シグネチャに変え、呼び出し側 (`SendDataToDestinationAsync`) が `ArrayPool<byte>.Shared` から scratch buffer を rent → builder へ書き込み → `Transport.SendAsync` に slice を渡す → `finally` で `Return`。Lifetime は `SendAsync` 呼び出し中に限定し、await を越えて pool buffer を保持しない。fragmentation 経路も同じ scratch buffer を 1 packet ずつ書き直して送る。

**Tech Stack:** C# / .NET 8, `System.Buffers.ArrayPool<T>`, 既存 `xunit` テスト, 既存 `tools/rosettadds-perf-runner`

---

## File Structure

修正対象:

- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` — `BuildDataPacket` /
  `SendDataToDestinationAsync` を ArrayPool 経由に。fragmentation 経路も同じ
  scratch buffer を再利用する実装に統合 (旧 `BuildDataPackets` /
  `BuildDataFragPackets` は削除し、新 `SendDataFragPacketsSequentialAsync` に
  インライン化)

追加テスト:

- `tests/rosettadds.Tests/Integration/PublisherHotPathTests.cs` (新規) —
  smoke test。LoopbackHub 経由で 1000 件 publish (reliable + small) と
  8 KiB payload (best-effort、fragmentation 経路) を確認

修正不要だが pass を確認する:

- `tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs`
- `tests/rosettadds.Tests/Rtps/ParticipantRtpsReceiverTests.cs`
- `tests/rosettadds.Tests/Integration/PubSubLoopbackTests.cs`

`StatelessWriter` は本プランで触らない (デッドコード)。

perf 計測 (手動):

- `artifacts/perf/<run-id>/manifest.json` + `metrics.ndjson` を
  `tools/rosettadds-perf-runner` で再生成し baseline と比較

---

## Task 1: Publisher hot path の smoke test を追加

**Files:**
- Create: `tests/rosettadds.Tests/Integration/PublisherHotPathTests.cs`

- [ ] **Step 1: テストファイルを作成**

`tests/rosettadds.Tests/Integration/PublisherHotPathTests.cs` を新規作成し、xunit + FluentAssertions を使って次のテストを置く:

```csharp
using System.Net;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherHotPathTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private sealed class TestEnv
    {
        public required LoopbackHub Hub { get; init; }
        public required DomainParticipant ParticipantA { get; init; }
        public required DomainParticipant ParticipantB { get; init; }
    }

    private static TestEnv CreatePair()
    {
        var hub = new LoopbackHub();
        var multicastIp = IPAddress.Parse("239.255.0.1");
        var spdpLoc = Locator.FromUdpV4(multicastIp, 7400u);
        var userMcLoc = Locator.FromUdpV4(multicastIp, 7401u);

        var spdpA = hub.Create(spdpLoc);
        var spdpB = hub.Create(spdpLoc);
        var ucA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7411u));
        var ucB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7413u));
        var userMcA = hub.Create(userMcLoc);
        var userMcB = hub.Create(userMcLoc);
        var userUcA = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.1"), 7412u));
        var userUcB = hub.Create(Locator.FromUdpV4(IPAddress.Parse("10.0.0.2"), 7414u));

        var optionsA = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 1, EntityName = "node_a",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = spdpA,
            CustomUnicastTransport = ucA,
            CustomUserMulticastTransport = userMcA,
            CustomUserUnicastTransport = userUcA,
        };
        var optionsB = new DomainParticipantOptions
        {
            DomainId = 0, ParticipantId = 2, EntityName = "node_b",
            MulticastGroup = multicastIp,
            SpdpInterval = TimeSpan.FromMilliseconds(50),
            SedpInterval = TimeSpan.FromMilliseconds(50),
            CustomMulticastTransport = spdpB,
            CustomUnicastTransport = ucB,
            CustomUserMulticastTransport = userMcB,
            CustomUserUnicastTransport = userUcB,
        };
        return new TestEnv
        {
            Hub = hub,
            ParticipantA = new DomainParticipant(optionsA),
            ParticipantB = new DomainParticipant(optionsB),
        };
    }

    [Fact]
    public async Task Reliable_small_payload_を_1000件_publish_すると全件_順序通り_受信できる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var received = new List<int>();
        var lockObj = new object();
        using var sub = pB.CreateSubscription<StringMessage>(
            "perf_topic",
            StringMessageSerializer.Instance,
            (msg, _) =>
            {
                if (int.TryParse(msg.Data, out var v))
                {
                    lock (lockObj) { received.Add(v); }
                }
            },
            reliability: ReliabilityQos.Reliable);

        using var pub = pA.CreatePublisher<StringMessage>(
            "perf_topic",
            StringMessageSerializer.Instance,
            reliability: ReliabilityQos.Reliable);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        const int N = 1000;
        for (int i = 0; i < N; i++)
        {
            await pub.PublishAsync(new StringMessage(i.ToString()));
        }

        var deadline = DateTime.UtcNow + ReceiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj) { if (received.Count == N) break; }
            await Task.Delay(10);
        }
        lock (lockObj)
        {
            received.Should().HaveCount(N);
            received.Should().Equal(Enumerable.Range(0, N));
        }
    }

    [Fact]
    public async Task BestEffort_large_payload_でも_publish_が_完了する()
    {
        // 1 メッセージが MTU を超える payload に対する fragmentation 経路の smoke。
        // LoopbackTransport には MTU 制約はないが、コードパスが単一 packet と
        // fragmentation の両方で実行されることを確認する。
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        var receivedTcs = new TaskCompletionSource<StringMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = pB.CreateSubscription<StringMessage>(
            "large_topic",
            StringMessageSerializer.Instance,
            (msg, _) => receivedTcs.TrySetResult(msg),
            reliability: ReliabilityQos.BestEffort);

        using var pub = pA.CreatePublisher<StringMessage>(
            "large_topic",
            StringMessageSerializer.Instance,
            reliability: ReliabilityQos.BestEffort);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        var big = new StringMessage(new string('x', 8192));
        await pub.PublishAsync(big);

        var received = await receivedTcs.Task.WaitAsync(ReceiveTimeout);
        received.Data.Length.Should().Be(8192);
    }
}
```

- [ ] **Step 2: テストが pass することを確認**

Run:

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS-perf-publisher-bottleneck
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PublisherHotPathTests" -v minimal
```

Expected: `Passed: 2` (両テスト green)。`LoopbackHub` 経路で publish/subscribe が成立することの baseline。

- [ ] **Step 3: Commit**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS-perf-publisher-bottleneck
git add tests/rosettadds.Tests/Integration/PublisherHotPathTests.cs
git commit -m "test: publisher hot path の smoke テストを追加"
```

---

## Task 2: `StatefulWriter` 単一 packet 経路を ArrayPool 化

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs:499-547,571-593`

- [ ] **Step 1: `BuildDataPacket` を `Span<byte>` 受け取りに変更**

`StatefulWriter.cs` の `BuildDataPacket` メソッド (L571-593) を以下に置換:

```csharp
/// <summary>DATA メッセージ (INFO_TS + DATA) を組み立てる。</summary>
private void BuildDataPacket(
    CacheChange change,
    EntityId readerEntityId,
    ReadOnlyMemory<byte> inlineQos,
    bool isAlive,
    Span<byte> destination,
    out int written)
{
    var writer = new RtpsMessageWriter(destination, _version, _vendorId, _localPrefix);
    writer.WriteInfoTimestamp(new InfoTimestampSubmessage(change.SourceTimestamp));
    var data = new DataSubmessage(
        readerEntityId: readerEntityId,
        writerEntityId: _writerEntityId,
        writerSn: change.SequenceNumber,
        serializedPayload: change.SerializedPayload,
        inlineQos: inlineQos,
        dataPresent: isAlive,
        keyPresent: !isAlive);
    writer.WriteData(data);
    written = writer.BytesWritten;
}
```

戻り値を `byte[]` から `out int written` に変える。Destination サイズが不足している場合は `RtpsMessageWriter` 内の `WriteSubmessage` が現状どおり `InvalidOperationException` を投げる (L85-89) ので挙動は変わらない。

- [ ] **Step 2: `BuildDataPackets` を削除し `SendDataToDestinationAsync` を ArrayPool 経由に書き換え**

`StatefulWriter.cs` の `BuildDataPackets` (L549-568) と `SendDataToDestinationAsync` (L533-547) を以下に置換:

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
            BuildDataPacket(change, readerEntityId, inlineQos, isAlive, scratch, out int written);
            await _transport.SendAsync(scratch.AsMemory(0, written), destination, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await SendDataFragPacketsSequentialAsync(
            change, readerEntityId, inlineQos, isAlive, scratch, destination, cancellationToken)
            .ConfigureAwait(false);
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

private async ValueTask SendDataFragPacketsSequentialAsync(
    CacheChange change,
    EntityId readerEntityId,
    ReadOnlyMemory<byte> inlineQos,
    bool isAlive,
    byte[] scratch,
    Locator destination,
    CancellationToken cancellationToken)
{
    if (change.SerializedPayload.Length == 0)
    {
        return;
    }
    int firstFragmentCapacity = SendBufferSize
        - RtpsHeader.Size
        - SubmessageHeader.Size
        - DataFragSubmessage.FixedHeaderSize
        - inlineQos.Length;
    int payloadFragmentSize = Math.Min(DataFragPayloadSize, firstFragmentCapacity);
    if (payloadFragmentSize <= 0)
    {
        throw new InvalidOperationException(
            $"DATA_FRAG inline QoS length {inlineQos.Length} leaves no room for payload.");
    }

    int fragmentCount = (change.SerializedPayload.Length + payloadFragmentSize - 1) / payloadFragmentSize;
    ushort fragmentSize = checked((ushort)payloadFragmentSize);
    uint sampleSize = checked((uint)change.SerializedPayload.Length);

    for (int i = 0; i < fragmentCount; i++)
    {
        int offset = i * payloadFragmentSize;
        int length = Math.Min(payloadFragmentSize, change.SerializedPayload.Length - offset);
        var fragmentPayload = change.SerializedPayload.Slice(offset, length);
        var fragmentInlineQos = i == 0 ? inlineQos : default;
        var dataFrag = new DataFragSubmessage(
            readerEntityId: readerEntityId,
            writerEntityId: _writerEntityId,
            writerSn: change.SequenceNumber,
            fragmentStartingNumber: checked((uint)i + 1u),
            fragmentsInSubmessage: 1,
            fragmentSize: fragmentSize,
            sampleSize: sampleSize,
            serializedPayloadFragment: fragmentPayload,
            inlineQos: fragmentInlineQos,
            keyPresent: !isAlive);

        var writer = new RtpsMessageWriter(scratch, _version, _vendorId, _localPrefix);
        writer.WriteDataFrag(dataFrag);
        int written = writer.BytesWritten;
        try
        {
            await _transport.SendAsync(scratch.AsMemory(0, written), destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error("StatefulWriter DATA_FRAG send failed", ex);
        }
    }
}
```

- [ ] **Step 3: `using System.Buffers;` を追加**

`StatefulWriter.cs` の先頭 using ブロックに `using System.Buffers;` を追加する (ArrayPool のため)。

- [ ] **Step 4: ビルドと既存テスト**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS-perf-publisher-bottleneck
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj -v minimal
```

Expected: build 0 error, 全テスト pass。
特に以下が pass していること:
- `PublisherHotPathTests.Reliable_small_payload_を_1000件_publish_すると全件_順序通り_受信できる`
- `PublisherHotPathTests.BestEffort_large_payload_でも_publish_が_完了する`
- `PubSubLoopbackTests.*` (10+ テスト)
- `StatefulHandshakeTests.*`
- `ParticipantRtpsReceiverTests.*`

- [ ] **Step 5: Commit**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS-perf-publisher-bottleneck
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "perf: StatefulWriter DATA 送信を ArrayPool 経由に"
```

---

## Task 3: perf 回帰計測

**Files:** (新規ファイルなし、計測のみ)

- [ ] **Step 1: baseline metrics.ndjson の値を控える**

`artifacts/perf/20260621-034324/unity-to-ros2-reliable-32/metrics.ndjson` の `measure_done` event 値:

- `messages_per_second`: 6851.38925619948
- `elapsed_ms`: 72.9779
- `serialized_bytes_per_message`: 41

`unity-to-ros2-best-effort-8192/metrics.ndjson` (これも fragmentation 経路):

- `messages_per_second`: 1606.92262265841
- `elapsed_ms`: 124.4615

これらを baseline として控える。

- [ ] **Step 2: `nix develop` に入る**

```bash
cd /home/ojii3/src/github.com/ojii3/ROSettaDDS-perf-publisher-bottleneck
nix develop
```

ROS 2 Humble 環境と .NET 8、uloop が使えるシェルに入る。`direnv` 設定済みなら `cd` するだけで OK。

- [ ] **Step 3: helper を build**

```bash
scripts/ros2/build_helper.sh
```

Expected: `tools/ros2-perf-helper/install/rosettadds_ros2_perf_helper/lib/rosettadds_ros2_perf_helper/ros2_perf_helper` が生成される。

- [ ] **Step 4: Unity Editor を起動して uloop 接続を確認**

別ターミナルで Unity Editor を `Ros2Unity` プロジェクトで開く (Linux 環境 + `unityhub` 等で起動)。Editor が起動したら `Ros2Unity/Assets/Scenes/SampleScene.unity` が enabled であることを確認。

Editor 起動後、シェルから:

```bash
uloop execute-dynamic-code --project-path Ros2Unity --code 'return "ok";'
```

Expected: stdout に `{"Success": true, ...}` を含む JSON。失敗したら Editor の uloop 接続を確認。

- [ ] **Step 5: perf runner を 1 scenario 実行**

```bash
dotnet run --project tools/rosettadds-perf-runner -- \
  --scenario unity-to-ros2-reliable-32 \
  --capture-frames 600
```

Expected: exit 0、`artifacts/perf/<新しい run-id>/unity-to-ros2-reliable-32/metrics.ndjson` に `measure_done` event が出力される。

- [ ] **Step 6: baseline と比較**

最新の `metrics.ndjson` から `messages_per_second` を読む:

```bash
LATEST=$(ls -1t artifacts/perf | head -1)
cat "artifacts/perf/$LATEST/unity-to-ros2-reliable-32/metrics.ndjson" | \
  python3 -c "import json,sys; print([json.loads(l) for l in sys.stdin if json.loads(l).get('event')=='measure_done'][0])"
```

成功基準:
- `messages_per_second` >= 10000 (baseline 6851 の 1.5x)
- `serialized_bytes_per_message` == 41 (変化なし)
- `elapsed_ms` < 50 (baseline 73ms から短縮)

未達なら baseline との差分、commit ログ、メトリクスをログに残し原因を切り分ける。本スペックではこれ以上の追加修正は行わない (スペック外の余地として残す)。

- [ ] **Step 7: Commit (計測結果のサマリ + perf artifact は commit しない)**

`artifacts/` は計測生成物なので commit しない (`.gitignore` で除外済み)。追加 commit が必要な変更があれば別途コミットする。成功 / 未達の結果は PR description か別 issue に記録する。

---

## Self-Review

### 1. Spec カバレッジ

| Spec ゴール / 項目 | 担当タスク |
|---|---|
| `BuildDataPacket` の `new byte[]` 排除 | Task 2 |
| `BuildDataPacket` (frag) の `new byte[]` 排除 | Task 2 (SendDataFragPacketsSequentialAsync) |
| `BuildPacket` (Stateless) の `new byte[]` 排除 | スコープ外 (デッドコードのため) |
| `IRtpsTransport.SendAsync` の lifetime 規約尊重 | Task 2 (scratch を `SendAsync` 完了後に `Return`) |
| 既存テストが pass | Task 1 (smoke), Task 2 (verify), Task 3 (perf) |
| perf 回帰確認 (1.5x) | Task 3 |
| 段階的実装 (smoke → refactor → 計測) | Task 1-3 |
| コミット粒度 (各タスクで 1 commit) | Task 1 Step 3, Task 2 Step 5 |

### 2. Placeholder スキャン

- "TODO" / "TBD" / "fix later" なし
- すべてのコードブロックに完全な実装を記載
- 「適切なエラーハンドリング」等の曖昧な指示なし。`catch (OperationCanceledException) { throw; } catch (Exception ex) { _logger.Error(...) }` パターンは元コードと一致

### 3. Type / 名前の一貫性

- `BuildDataPacket(Span<byte> destination, out int written)` を Task 2 で導入、Task 2 内で `BuildDataPackets` を `SendDataFragPacketsSequentialAsync` に置換
- `scratch` 変数を Task 2 で一貫使用、`ArrayPool<byte>.Shared.Rent(SendBufferSize)` → `Return(scratch)` のパターンを統一
- `_logger.Error` メッセージの文言は元コード (`"StatefulWriter DATA send failed"` / `"StatefulWriter DATA_FRAG send failed"`) を維持
- `measure_done` event の `messages_per_second` フィールド名は spec と perf runner の両方で同一

### 4. スコープ確認

Task 1-3 は 1 つの独立したサブシステム (publisher hot path の allocation 削減) のみ。Heartbeat / Gap 経路、`SerializeWithEncapsulation`、async 段数削減、8 KiB best-effort 失敗、`StatelessWriter` (デッドコード) は意図的にスコープ外。spec 通り。
