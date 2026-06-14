# Volatile Pre-join Sample Suppression 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Volatile Stateful writer が late-join reliable reader に対し、pre-join サンプルを GAP で通知して再送しないよう修正し、issue #78 のフレーキーテストを CI で決定的にする。

**Architecture:** `ReaderProxy` に per-reader low watermark を追加し、`StatefulWriter.MatchReader` で Volatile+Reliable 時に writer 履歴の `LastSequenceNumber` を記録する。`ResendRequestedAsync` で watermark 以下の SN には GAP を返す。TransientLocal writer (`resendHistoryOnMatch=true`) と BestEffort reader は対象外 (既存挙動維持)。

**Tech Stack:** C# / .NET 8 / xunit + FluentAssertions (既存パターン踏襲)

**Design doc:** `docs/superpowers/specs/2026-06-14-volatile-pre-join-suppression-design.md`

**Issue:** https://github.com/OJII3/ROSettaDDS/issues/78

---

## 変更ファイル一覧

| 操作 | パス | 役割 |
| --- | --- | --- |
| Modify | `src/rosettadds/Rtps/Writer/ReaderProxy.cs` | low watermark の API 追加 |
| Modify | `src/rosettadds/Rtps/Writer/StatefulWriter.cs` | match 時に watermark 設定、resend 時に GAP 応答 |
| Modify | `docs/compatibility.md` | pre-join 抑止の実装済み記述に更新 |
| Create | `tests/rosettadds.Tests/Rtps/ReaderProxyLowWatermarkTests.cs` | `ReaderProxy` の watermark API 単体テスト |
| (Modify) | `tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs` | `StatefulWriter` 側の watermark 設定 + GAP 応答テストを追加 |

discovery / SEDP / transport / CDR 層は触らない。

---

## Task 1: `ReaderProxy` に low watermark API を追加 (TDD)

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/ReaderProxy.cs` (public API 追加)
- Create: `tests/rosettadds.Tests/Rtps/ReaderProxyLowWatermarkTests.cs`

- [ ] **Step 1.1: 失敗する単体テストを書く**

`tests/rosettadds.Tests/Rtps/ReaderProxyLowWatermarkTests.cs` を新規作成し、以下を記述する:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Rtps;

public class ReaderProxyLowWatermarkTests
{
    private static readonly Guid TestReaderGuid = new(
        GuidPrefix.Unknown, EntityId.Unknown);

    [Fact]
    public void 初期状態では_LowWatermark_未設定()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.IsLowWatermarkSet.Should().BeFalse();
        proxy.LowWatermark.Should().BeNull();
    }

    [Fact]
    public void SetLowWatermark_で_設定される()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.SetLowWatermark(new SequenceNumber(42L));

        proxy.IsLowWatermarkSet.Should().BeTrue();
        proxy.LowWatermark.Should().Be(new SequenceNumber(42L));
    }

    [Fact]
    public void SetLowWatermark_は_2回目以降_無視される()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.SetLowWatermark(new SequenceNumber(10L));
        proxy.SetLowWatermark(new SequenceNumber(20L));

        proxy.LowWatermark.Should().Be(new SequenceNumber(10L));
    }

    [Fact]
    public void IsPreJoin_は_未設定時_false()
    {
        var proxy = new ReaderProxy(TestReaderGuid);

        proxy.IsPreJoin(new SequenceNumber(1L)).Should().BeFalse();
        proxy.IsPreJoin(new SequenceNumber(0L)).Should().BeFalse();
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(5L, true)]
    [InlineData(10L, true)]
    [InlineData(11L, false)]
    [InlineData(100L, false)]
    public void IsPreJoin_は_watermark_以下なら_true(long sn, bool expected)
    {
        var proxy = new ReaderProxy(TestReaderGuid);
        proxy.SetLowWatermark(new SequenceNumber(10L));

        proxy.IsPreJoin(new SequenceNumber(sn)).Should().Be(expected);
    }

    [Fact]
    public void IsPreJoin_は_SN0_に対して_false()
    {
        var proxy = new ReaderProxy(TestReaderGuid);
        proxy.SetLowWatermark(new SequenceNumber(10L));

        proxy.IsPreJoin(new SequenceNumber(0L)).Should().BeFalse();
    }

    [Fact]
    public void Reliable_Reader_Proxy_は_生成できる()
    {
        var proxy = new ReaderProxy(TestReaderGuid, reliability: ReliabilityKind.Reliable);

        proxy.IsReliable.Should().BeTrue();
    }
}
```

- [ ] **Step 1.2: テストを走らせて失敗を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ReaderProxyLowWatermarkTests" --nologo
```

期待: コンパイル失敗 (`IsLowWatermarkSet` / `LowWatermark` / `SetLowWatermark` / `IsPreJoin` が未定義)。

- [ ] **Step 1.3: `ReaderProxy` に API を追加**

`src/rosettadds/Rtps/Writer/ReaderProxy.cs` の `_highestAcked` フィールドの直後 (約 17 行目付近) に以下を追加する:

```csharp
    private long _lowWatermark;
    private bool _lowWatermarkSet;
```

次に、ファイル末尾 (`ClearRequested` メソッドの後) に以下を追加する:

```csharp
    /// <summary>
    /// 初回 match 時点で writer 履歴の <c>LastSequenceNumber</c> を記録する。
    /// NACK されてきた SN がこの値以下なら「pre-join サンプル (late-join reader に無関連)」として
    /// 取り扱う。Volatile writer + reliable reader のときに writer 側で設定する。
    /// </summary>
    public bool IsLowWatermarkSet
    {
        get { lock (_lock) { return _lowWatermarkSet; } }
    }

    /// <summary>low watermark (未設定時は <c>null</c>)。</summary>
    public SequenceNumber? LowWatermark
    {
        get { lock (_lock) { return _lowWatermarkSet ? new SequenceNumber(_lowWatermark) : null; } }
    }

    /// <summary>
    /// low watermark を設定する。既に設定済みの呼び出しは最初の値を維持する (idempotent)。
    /// </summary>
    public void SetLowWatermark(SequenceNumber sn)
    {
        lock (_lock)
        {
            if (_lowWatermarkSet)
            {
                return;
            }
            _lowWatermark = sn.Value;
            _lowWatermarkSet = true;
        }
    }

    /// <summary>
    /// 指定 SN が pre-join サンプル (low watermark 以下で「無関連」) かどうか。
    /// 未設定、または SN が 0 (未初期化) のときは <c>false</c>。
    /// </summary>
    public bool IsPreJoin(SequenceNumber sn)
    {
        lock (_lock)
        {
            if (!_lowWatermarkSet)
            {
                return false;
            }
            if (sn.Value <= 0)
            {
                return false;
            }
            return sn.Value <= _lowWatermark;
        }
    }
```

- [ ] **Step 1.4: テストを走らせて成功を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ReaderProxyLowWatermarkTests" --nologo
```

期待: 8 件すべて PASS。

- [ ] **Step 1.5: コミット**

```bash
git add src/rosettadds/Rtps/Writer/ReaderProxy.cs tests/rosettadds.Tests/Rtps/ReaderProxyLowWatermarkTests.cs
git commit -m "feat(rtps): ReaderProxy に late-join 用 low watermark API を追加"
```

---

## Task 2: `StatefulWriter.MatchReader` で Volatile+Reliable 時に watermark を設定 (TDD)

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs` (`MatchReader` の proxy 追加直後)
- Modify: `tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs` (テスト追加)

- [ ] **Step 2.1: 失敗するテストを書く**

`tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs` の末尾 (`BuildDataFragPacket` の直前、`BuildAckNackPacket` などのヘルパーよりも前) に次のテストを追加する。`StatefulHandshakeTests` クラス内に追加すること:

```csharp
    [Fact]
    public void StatefulWriter_MatchReader_Volatile_Reliable_で_pre_join_watermark_が設定される()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        using var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: false);

        // match 時点で履歴に SN=5 まで入っている状況を再現
        for (int i = 1; i <= 5; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

        var proxy = writer.GetReaderProxy(readerGuid);
        proxy.Should().NotBeNull();
        proxy!.IsLowWatermarkSet.Should().BeTrue();
        proxy.LowWatermark.Should().Be(new SequenceNumber(5L));
    }

    [Fact]
    public void StatefulWriter_MatchReader_TransientLocal_では_watermark_未設定()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        using var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: true);

        for (int i = 1; i <= 5; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

        var proxy = writer.GetReaderProxy(readerGuid);
        proxy.Should().NotBeNull();
        proxy!.IsLowWatermarkSet.Should().BeFalse();
        proxy.LowWatermark.Should().BeNull();
    }

    [Fact]
    public void StatefulWriter_MatchReader_BestEffort_では_watermark_未設定()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        using var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: false);

        for (int i = 1; i <= 5; i++)
        {
            history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
        }

        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.BestEffort);

        var proxy = writer.GetReaderProxy(readerGuid);
        proxy.Should().NotBeNull();
        proxy!.IsLowWatermarkSet.Should().BeFalse();
    }

    [Fact]
    public void StatefulWriter_MatchReader_履歴空_で_watermark_未設定()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        using var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: false);

        // 履歴に何も書き込まない
        writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

        var proxy = writer.GetReaderProxy(readerGuid);
        proxy.Should().NotBeNull();
        proxy!.IsLowWatermarkSet.Should().BeFalse();
    }
```

- [ ] **Step 2.2: テストを走らせて失敗を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests.StatefulWriter_MatchReader" --nologo
```

期待: 4 件すべて FAIL。`watermark_` 系の assertion が通らない (`IsLowWatermarkSet` は初期 `false` のまま)。

- [ ] **Step 2.3: `StatefulWriter.MatchReader` を変更**

`src/rosettadds/Rtps/Writer/StatefulWriter.cs` の `MatchReader` メソッド内 (`addedProxy` を `_matched` に追加した直後のブロック) を以下に置き換える:

```csharp
        if (addedProxy is not null)
        {
            if (_resendHistoryOnMatch)
            {
                RunBackground(
                    token => SendHistoricalDataToReaderAsync(addedProxy, token),
                    "StatefulWriter historical DATA send");
            }
            else if (reliability == ReliabilityKind.Reliable)
            {
                // Pre-join sample suppression: Volatile writer + reliable reader のとき、
                // match 時点の writer 履歴 LastSequenceNumber を per-reader low watermark として記録する。
                // これにより NACK されてきた pre-join SN には GAP を返し、DATA では再送しない。
                var lastSn = _history.LastSequenceNumber;
                if (lastSn.Value > 0)
                {
                    addedProxy.SetLowWatermark(lastSn);
                    _logger.Debug(
                        $"StatefulWriter: pre-join low watermark {lastSn} set for reader {readerGuid} (Volatile)");
                }
            }
        }
```

- [ ] **Step 2.4: テストを走らせて成功を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests.StatefulWriter_MatchReader" --nologo
```

期待: 4 件すべて PASS。

- [ ] **Step 2.5: コミット**

```bash
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs
git commit -m "feat(rtps): Volatile writer が match 時に pre-join low watermark を記録"
```

---

## Task 3: `StatefulWriter.ResendRequestedAsync` で pre-join SN に GAP を返す (TDD)

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs` (`ResendRequestedAsync` 内)
- Modify: `tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs` (テスト追加)

- [ ] **Step 3.1: 失敗するテストを書く**

`tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs` の `StatefulWriter_MatchReader_履歴空_で_watermark_未設定` の直後に以下を追加する:

```csharp
    [Fact]
    public async Task Writer_は_pre_join_SN_の_NACK_に_DATA_ではなく_GAP_を返す()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: false);
        using (writer)
        {
            // match 時点で履歴に SN=1..3 を入れておく (LastSequenceNumber=3)
            for (int i = 1; i <= 3; i++)
            {
                history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
            }
            writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

            int dataCount = 0;
            int gapCount = 0;
            var gapTcs = new TaskCompletionSource<GapSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.ReaderTransport.Received += (packet, _) =>
            {
                if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _))
                {
                    return;
                }
                var reader = new RtpsMessageReader(packet.Span);
                while (reader.TryReadNext(out var header, out var body))
                {
                    if (header.Kind == SubmessageKind.Data)
                    {
                        Interlocked.Increment(ref dataCount);
                    }
                    else if (header.Kind == SubmessageKind.Gap)
                    {
                        Interlocked.Increment(ref gapCount);
                        gapTcs.TrySetResult(GapSubmessage.ReadBody(body, header.Endianness, header.Flags));
                    }
                }
            };

            // reader が SN=1 を NACK する想定で ACKNACK 送信 (bitmapBase=1, bit 0 set)
            var ackPacket = BuildAckNackPacket(
                s.ReaderPrefix,
                s.ReaderEntityId,
                s.WriterEntityId,
                new SequenceNumberSet(new SequenceNumber(1L), 1, new[] { 0x80000000u }));
            writer.ProcessPacket(ackPacket);

            var gap = await gapTcs.Task.WaitAsync(ReceiveTimeout);
            gap.GapStart.Value.Should().Be(1L);
            gap.GapList.BitmapBase.Value.Should().Be(2L);
            Volatile.Read(ref dataCount).Should().Be(0, "pre-join サンプルは GAP で返すべき");
            Volatile.Read(ref gapCount).Should().Be(1);
        }
    }

    [Fact]
    public async Task Writer_は_post_join_SN_の_NACK_には_DATA_を返す()
    {
        var s = CreateSetup();
        var writerGuid = new Guid(s.WriterPrefix, s.WriterEntityId);
        var readerGuid = new Guid(s.ReaderPrefix, s.ReaderEntityId);
        var history = new WriterHistoryCache(writerGuid);
        var writer = new StatefulWriter(
            sendTransport: s.WriterTransport,
            multicastDestination: s.ReaderLocator,
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: s.WriterPrefix,
            writerEntityId: s.WriterEntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(50),
            history: history,
            resendHistoryOnMatch: false);
        using (writer)
        {
            // match 時点で履歴に SN=1..5 を入れておく (LastSequenceNumber=5、watermark=5)
            for (int i = 1; i <= 5; i++)
            {
                history.Add(ChangeKind.Alive, new byte[] { (byte)i }, Time.Now());
            }
            writer.MatchReader(readerGuid, s.ReaderLocator, ReliabilityKind.Reliable);

            var dataTcs = new TaskCompletionSource<DataSubmessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.ReaderTransport.Received += (packet, _) =>
            {
                if (!RtpsHeader.TryRead(packet.Span, out _, out _, out _))
                {
                    return;
                }
                var reader = new RtpsMessageReader(packet.Span);
                while (reader.TryReadNext(out var header, out var body))
                {
                    if (header.Kind == SubmessageKind.Data)
                    {
                        dataTcs.TrySetResult(DataSubmessage.ReadBody(body, header.Endianness, header.Flags));
                    }
                }
            };

            // reader が SN=6 を NACK する想定で ACKNACK 送信 (bitmapBase=6, bit 0 set) — post-join 側
            var ackPacket = BuildAckNackPacket(
                s.ReaderPrefix,
                s.ReaderEntityId,
                s.WriterEntityId,
                new SequenceNumberSet(new SequenceNumber(6L), 1, new[] { 0x80000000u }));
            writer.ProcessPacket(ackPacket);

            var data = await dataTcs.Task.WaitAsync(ReceiveTimeout);
            data.WriterSequenceNumber.Value.Should().Be(6L);
        }
    }
```

- [ ] **Step 3.2: テストを走らせて失敗を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests.Writer_は_pre_join_SN_の_NACK_に" --nologo
```

期待: `Writer_は_pre_join_SN_の_NACK_に_DATA_ではなく_GAP_を返す` が FAIL。`pre-join サンプルは GAP で返すべき` のアサーションが現状の DATA 送信動作で失敗する。

- [ ] **Step 3.3: `ResendRequestedAsync` を変更**

`src/rosettadds/Rtps/Writer/StatefulWriter.cs` の `ResendRequestedAsync` メソッド (約 579 行目) を以下に置き換える:

```csharp
    private async Task ResendRequestedAsync(ReaderProxy proxy, CancellationToken cancellationToken)
    {
        var requested = proxy.RequestedSequenceNumbers();
        if (requested.Count == 0) return;
        foreach (var sn in requested)
        {
            var dest = proxy.UnicastLocator ?? _multicastDestination;
            if (proxy.IsPreJoin(sn))
            {
                // Volatile pre-join suppression: watermark 以下の SN は DATA ではなく GAP で応答。
                // reader は GAP 受理後、当該 SN を NACK しなくなる。
                await SendGapToDestinationAsync(
                    sn,
                    proxy.ReaderGuid.EntityId,
                    dest,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var change = _history.Get(sn);
                if (change is null)
                {
                    await SendGapToDestinationAsync(
                        sn,
                        proxy.ReaderGuid.EntityId,
                        dest,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendDataToDestinationAsync(change, proxy.ReaderGuid.EntityId, dest, cancellationToken).ConfigureAwait(false);
                }
            }
            proxy.ClearRequested(sn);
        }
    }
```

- [ ] **Step 3.4: テストを走らせて成功を確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests.Writer_は_pre_join_SN_の_NACK_に|FullyQualifiedName~StatefulHandshakeTests.Writer_は_post_join_SN_の_NACK_には" --nologo
```

期待: 2 件すべて PASS。

- [ ] **Step 3.5: 既存テストの regression 確認**

`StatefulHandshakeTests` 全体 + `Writer_は_history_に無い要求SNへ_GAP_を返す` を含めて、エラーなく全件通過することを確認:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~StatefulHandshakeTests" --nologo
```

期待: 既存テストも含めて全件 PASS。

- [ ] **Step 3.6: コミット**

```bash
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs tests/rosettadds.Tests/Rtps/StatefulHandshakeTests.cs
git commit -m "feat(rtps): pre-join SN への NACK には GAP を返す (Volatile 履歴非再送)"
```

---

## Task 4: 既存のフレーキー統合テストの決定性確認

**Files:**
- (変更なし、`dotnet test` で確認のみ)

- [ ] **Step 4.1: 対象テストを明示的に走らせる**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PubSubLoopbackTests.late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない" --nologo
```

期待: PASS。

- [ ] **Step 4.2: 連続実行で安定性を確認**

ローカルの開発環境で同じテストを 50 回連続実行し、フレーキー (非決定的な失敗) が発生しないことを確認する。bash のワンライナー:

```bash
for i in $(seq 1 50); do
  dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj \
    --filter "FullyQualifiedName~PubSubLoopbackTests.late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない" \
    --nologo --verbosity quiet > /tmp/loopback-test-$$.log 2>&1
  if [ $? -ne 0 ]; then
    echo "FAIL on iteration $i"
    tail -40 /tmp/loopback-test-$$.log
    exit 1
  fi
done
echo "50 iterations OK"
```

期待: 50 回とも PASS (`50 iterations OK` が出力される)。

- [ ] **Step 4.3: 関連する TransientLocal テストの regression 確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~PubSubLoopbackTests.late_Subscription_は_TRANSIENT_LOCAL|FullyQualifiedName~PubSubLoopbackTests.chatter_を_Publisher_から" --nologo
```

期待: 両テストとも PASS (Volatile 経路が本 fix で壊れていないことの確認)。

- [ ] **Step 4.4: コミット (変更なしの場合はスキップ)**

このタスクでコード変更がないため、コミットは不要。失敗した場合は原因を切り戻して Step 3 をやり直す。

---

## Task 5: `docs/compatibility.md` の記述を更新

**Files:**
- Modify: `docs/compatibility.md` (56-58 行目の引用ブロック)

- [ ] **Step 5.1: 引用ブロックを実装済みの記述に置き換える**

`docs/compatibility.md` の "QoS と history depth の現状" セクション末尾の引用ブロック:

```markdown
> Volatile reader への late-join 抑止は未実装。Volatile publisher は新規サンプルを reader に積極再送しないが、
> Reliable reader が HEARTBEAT に対し未受信 SN を NACK した場合、join 前のサンプルが再送され得る
> (GAP による pre-join sample の明示的除外は今後の課題)。
```

を以下に置き換える:

```markdown
> Volatile Stateful writer は late-join reliable reader に対し、match 時点で writer 履歴の
> `LastSequenceNumber` を per-reader low watermark として保持する。NACK されてきた pre-join
> サンプル (SN ≤ watermark) には GAP を返して「無関連」と通知し、DATA では再送しない。
> TransientLocal writer の履歴再送経路 (`resendHistoryOnMatch=true` 経路) は本対象外。
> 検証は `PubSubLoopbackTests.late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない`。
```

- [ ] **Step 5.2: ドキュメント差分のセルフレビュー**

```bash
git diff docs/compatibility.md
```

期待: 引用ブロックの中身のみが置換され、前後の文や他のセクションに影響がないこと。誤って他の記述を消していないか確認する。

- [ ] **Step 5.3: コミット**

```bash
git add docs/compatibility.md
git commit -m "docs: pre-join sample suppression 実装済みを compatibility.md に反映"
```

---

## Task 6: 全体回帰とビルド確認

**Files:**
- (変更なし、`dotnet build` / `dotnet test` で確認のみ)

- [ ] **Step 6.1: フルビルド確認**

```bash
dotnet build rosettadds.sln --nologo
```

期待: 0 warning、0 error。

- [ ] **Step 6.2: 全テスト実行 (net8.0)**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --nologo
```

期待: すべて PASS。失敗が出た場合は `systematic-debugging` スキルで原因を切り分けてから修正する。

- [ ] **Step 6.3: netstandard2.1 ビルド確認 (Unity 互換性チェック)**

```bash
dotnet build src/rosettadds/rosettadds.csproj -f netstandard2.1 --nologo
```

期待: 0 warning、0 error。Unity Editor 6000.3 + .NET Standard 2.1 の互換性に影響しないことを確認。

- [ ] **Step 6.4: コミットログ確認と PR 準備**

```bash
git log --oneline 128bf98..HEAD
```

期待: 4 コミット (Task 1, 2, 3, 5) が積まれている。Task 4 / Task 6 は確認のみでコミットなし。git status が clean であることを `git status` で確認。

---

## 完了条件 (Definition of Done)

- [ ] Task 1-3 の TDD サイクルがすべて green
- [ ] Task 4 の 50 連続実行でフレーキー再現なし
- [ ] `docs/compatibility.md` の記述が実装と一致
- [ ] `dotnet build rosettadds.sln` 0 warning
- [ ] `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj` すべて PASS
- [ ] `dotnet build src/rosettadds/rosettadds.csproj -f netstandard2.1` 0 warning

## スコープ外 (本 plan では実施しない)

- match 時の proactive GAP 送信 (案 B、別 issue)
- `LoopbackHub` ベースの GAP パケット capture 低レベルテスト
- `UserWriterHeartbeatPeriod` をテストで制御可能にする API 整備
