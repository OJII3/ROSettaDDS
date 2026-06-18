# WaitForMatched API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Publisher<T>` / `Subscription<T>` に Fast DDS 互換のマッチ status 構造体 (`PublicationMatchedStatus` / `SubscriptionMatchedStatus`) と `WaitForMatchedAsync(int minCount, TimeSpan timeout, CancellationToken)` を追加し、issue #83 の `DiscoveryDb` 直覗きワークアラウンドを置き換える。

**Architecture:** RTPS 層 (`StatefulWriter` / `StatefulReader` / `StatelessReader`) に累計マッチ数管理と last-handle トラッキングを追加し、Fast DDS 互換の Status 構造体 (`MatchedStatus.cs`) を介して `Publisher<T>` / `Subscription<T>` に公開する。`WaitForMatchedAsync` は `MatchWaiter` (内部静的ヘルパ) で 20ms 間隔の polling を行う。BestEffort 経路は `IUserReader` を 4 プロパティ拡張して `MatchedWriterCount` 相当を整備する。`total_count_change` / `current_count_change` は Fast DDS 互換で read 時にリセットする。

**Tech Stack:** C# / .NET (netstandard2.1, net8.0), xUnit + FluentAssertions。ビルド `dotnet build rosettadds.sln`、テスト `dotnet test`。

**仕様書:** `docs/superpowers/specs/2026-06-19-wait-for-matched-api-design.md`

**ブランチ:** `feat/wait-for-matched-api`（作成済み）

---

## ファイル構成

新規作成:
- `src/rosettadds/Dds/MatchedStatus.cs` — `PublicationMatchedStatus` / `SubscriptionMatchedStatus` 構造体
- `src/rosettadds/Dds/MatchWaiter.cs` — `WaitUntilMatchedAsync` 内部静的ヘルパ
- `tests/rosettadds.Tests/Dds/MatchWaiterTests.cs` — polling ヘルパの単体テスト
- `tests/rosettadds.Tests/Integration/PublisherSubscriptionMatchedTests.cs` — loopback 結合テスト

変更:
- `src/rosettadds/Dds/IUserReader.cs` — 4 プロパティ追加
- `src/rosettadds/Dds/ReliableUserReader.cs` — IUserReader 実装
- `src/rosettadds/Dds/BestEffortUserReader.cs` — IUserReader 実装
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` — 累計マッチ数と status
- `src/rosettadds/Rtps/Reader/StatefulReader.cs` — 累計マッチ数と status
- `src/rosettadds/Rtps/Reader/StatelessReader.cs` — 累計マッチ数と status
- `src/rosettadds/Dds/Publisher.cs` — status + WaitForMatchedAsync
- `src/rosettadds/Dds/Subscription.cs` — status + WaitForMatchedAsync
- `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs` — ワークアラウンド削除

---

## Phase 1: Status 構造体 + IUserReader 拡張

### Task 1: MatchedStatus.cs を新規作成

**Files:**
- Create: `src/rosettadds/Dds/MatchedStatus.cs`

- [ ] **Step 1: ファイル作成**

```csharp
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// Publication マッチ状態 (Fast DDS PublicationMatchedStatus 互換)。
/// <see cref="Publisher{T}.PublicationMatchedStatus"/> から取得する。
/// <para>
/// <c>TotalCountChange</c> と <c>CurrentCountChange</c> は「最後にこの構造体を
/// read したときからの差分」を表し、read 時に 0 にリセットされる
/// (Fast DDS <c>get_publication_matched_status</c> 互換)。
/// </para>
/// </summary>
public readonly struct PublicationMatchedStatus
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }

    /// <summary>最後にマッチした remote reader の GUID (ある場合)。</summary>
    public Guid? LastSubscriptionHandle { get; init; }
}

/// <summary>
/// Subscription マッチ状態 (Fast DDS SubscriptionMatchedStatus 互換)。
/// <see cref="Subscription{T}.SubscriptionMatchedStatus"/> から取得する。
/// <para>
/// <c>TotalCountChange</c> と <c>CurrentCountChange</c> は「最後にこの構造体を
/// read したときからの差分」を表し、read 時に 0 にリセットされる
/// (Fast DDS <c>get_subscription_matched_status</c> 互換)。
/// </para>
/// </summary>
public readonly struct SubscriptionMatchedStatus
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }

    /// <summary>最後にマッチした remote writer の GUID (ある場合)。</summary>
    public Guid? LastPublicationHandle { get; init; }
}
```

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Dds/MatchedStatus.cs
git commit -m "feat(dds): Fast DDS 互換の MatchedStatus 構造体を追加"
```

---

### Task 2: StatefulWriter に累計マッチ数と status プロパティ追加

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`

- [ ] **Step 1: フィールドを追加**

`StatefulWriter` クラスの `_matchedLock` 宣言の直後付近に以下を追加する:

```csharp
    private long _totalMatchedReaders;        // lifetime 累計 (lifetime monotonic)
    private Guid? _lastSubscriptionHandle;    // 最後に新規マッチした reader
    private int _lastReportedCurrentReaders;  // read リセット用
    private long _lastReportedTotalReaders;
```

- [ ] **Step 2: MatchReader で累計を更新**

`StatefulWriter.MatchReader` 内の `lock (_matchedLock) { ... }` ブロック末尾で、新規追加 (`addedProxy is not null` 判定の前) に以下を追加:

```csharp
            if (addedProxy is not null)
            {
                _totalMatchedReaders++;
                _lastSubscriptionHandle = readerGuid;
            }
```

つまり現状の `if (addedProxy is not null) { ... }` ブロック内に 2 行を差し込む。

- [ ] **Step 3: PublicationMatchedStatus プロパティを追加**

`StatefulWriter` クラス内の `MatchedReaders` プロパティの直後に追加する:

```csharp
    public PublicationMatchedStatus PublicationMatchedStatus
    {
        get
        {
            int current;
            long total;
            Guid? lastHandle;
            lock (_matchedLock)
            {
                current = _matched.Count;
                total = _totalMatchedReaders;
                lastHandle = _lastSubscriptionHandle;
            }
            var status = new PublicationMatchedStatus
            {
                CurrentCount = current,
                CurrentCountChange = current - _lastReportedCurrentReaders,
                TotalCount = checked((int)Math.Min(total, int.MaxValue)),
                TotalCountChange = checked((int)Math.Min(total - _lastReportedTotalReaders, int.MaxValue)),
                LastSubscriptionHandle = lastHandle,
            };
            _lastReportedCurrentReaders = current;
            _lastReportedTotalReaders = total;
            return status;
        }
    }
```

ファイル先頭に `using ROSettaDDS.Dds;` (構造体を使うため) を追加。`MatchReader` 内の変更で使う `Guid?` は既に `using Guid = ROSettaDDS.Common.Guid;` 経由で使える。

- [ ] **Step 4: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs
git commit -m "feat(rtps): StatefulWriter に PublicationMatchedStatus を追加"
```

---

### Task 3: StatefulReader に累計マッチ数と status プロパティ追加

**Files:**
- Modify: `src/rosettadds/Rtps/Reader/StatefulReader.cs`

- [ ] **Step 1: フィールドを追加**

`StatefulReader` クラスの `_matched` 宣言の直後に以下を追加:

```csharp
    private long _totalMatchedWriters;
    private Guid? _lastPublicationHandle;
    private int _lastReportedCurrentWriters;
    private long _lastReportedTotalWriters;
```

- [ ] **Step 2: MatchWriter で累計を更新**

`StatefulReader.MatchWriter` 内の `lock (_matchedLock) { ... }` ブロックの else 分岐 (`_matched[writerGuid] = new WriterProxy(writerGuid, unicastReplyLocator);` の直前) で、新規追加の場合のみ累計を増やす。`existing` が取得できなかった (新規) ときだけ:

```csharp
            else
            {
                _matched[writerGuid] = new WriterProxy(writerGuid, unicastReplyLocator);
                _totalMatchedWriters++;
                _lastPublicationHandle = writerGuid;
            }
```

- [ ] **Step 3: SubscriptionMatchedStatus プロパティを追加**

`MatchedWriters` プロパティの直後に追加:

```csharp
    public SubscriptionMatchedStatus SubscriptionMatchedStatus
    {
        get
        {
            int current;
            long total;
            Guid? lastHandle;
            lock (_matchedLock)
            {
                current = _matched.Count;
                total = _totalMatchedWriters;
                lastHandle = _lastPublicationHandle;
            }
            var status = new SubscriptionMatchedStatus
            {
                CurrentCount = current,
                CurrentCountChange = current - _lastReportedCurrentWriters,
                TotalCount = checked((int)Math.Min(total, int.MaxValue)),
                TotalCountChange = checked((int)Math.Min(total - _lastReportedTotalWriters, int.MaxValue)),
                LastPublicationHandle = lastHandle,
            };
            _lastReportedCurrentWriters = current;
            _lastReportedTotalWriters = total;
            return status;
        }
    }
```

ファイル先頭に `using ROSettaDDS.Dds;` を追加。

- [ ] **Step 4: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rtps/Reader/StatefulReader.cs
git commit -m "feat(rtps): StatefulReader に SubscriptionMatchedStatus を追加"
```

---

### Task 4: StatelessReader に累計マッチ数と status プロパティ追加

**Files:**
- Modify: `src/rosettadds/Rtps/Reader/StatelessReader.cs`

- [ ] **Step 1: フィールドを追加**

`StatelessReader` クラスの `_matchedWriters` 宣言の直後に以下を追加:

```csharp
    private long _totalMatchedWriters;
    private Guid? _lastPublicationHandle;
    private int _lastReportedCurrentWriters;
    private long _lastReportedTotalWriters;
```

- [ ] **Step 2: MatchWriter で累計を更新**

`StatelessReader.MatchWriter` 内の `lock (_matchedLock) { ... }` ブロックで `_matchedWriters.Add(writerGuid);` を、`Add` の戻り値 (新規追加で true) を使った判定に置換する:

```csharp
            lock (_matchedLock)
            {
                if (_matchedWriters.Add(writerGuid))
                {
                    _totalMatchedWriters++;
                    _lastPublicationHandle = writerGuid;
                }
            }
```

`lock (_deliveryLock) { ... }` ブロック内の他の処理 (pending payload の drain 等) は現状維持。

- [ ] **Step 3: SubscriptionMatchedStatus プロパティを追加**

`MatchWriter` メソッドの直後 (または `UnmatchWriter` の直前) に追加:

```csharp
    public int MatchedWriterCount
    {
        get { lock (_matchedLock) { return _matchedWriters.Count; } }
    }

    public SubscriptionMatchedStatus SubscriptionMatchedStatus
    {
        get
        {
            int current;
            long total;
            Guid? lastHandle;
            lock (_matchedLock)
            {
                current = _matchedWriters.Count;
                total = _totalMatchedWriters;
                lastHandle = _lastPublicationHandle;
            }
            var status = new SubscriptionMatchedStatus
            {
                CurrentCount = current,
                CurrentCountChange = current - _lastReportedCurrentWriters,
                TotalCount = checked((int)Math.Min(total, int.MaxValue)),
                TotalCountChange = checked((int)Math.Min(total - _lastReportedTotalWriters, int.MaxValue)),
                LastPublicationHandle = lastHandle,
            };
            _lastReportedCurrentWriters = current;
            _lastReportedTotalWriters = total;
            return status;
        }
    }
```

ファイル先頭に `using ROSettaDDS.Dds;` を追加。

- [ ] **Step 4: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 5: 既存テストが緑のままか確認**

Run: `dotnet test --filter "Rtps"`
Expected: PASS（既存 MatchWriter 経路に影響なし）。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Rtps/Reader/StatelessReader.cs
git commit -m "feat(rtps): StatelessReader に SubscriptionMatchedStatus を追加"
```

---

### Task 5: IUserReader 拡張

**Files:**
- Modify: `src/rosettadds/Dds/IUserReader.cs`

- [ ] **Step 1: 4 プロパティを追加**

`IUserReader` インタフェースに以下を追加する。`Guid Start()` と `Guid Stop()` の間あたりに:

```csharp
    /// <summary>現在マッチ中の writer 数 (current_count 相当)。</summary>
    int MatchedWriterCount { get; }

    /// <summary>
    /// Subscription matched status snapshot (Fast DDS SubscriptionMatchedStatus 互換)。
    /// </summary>
    SubscriptionMatchedStatus SubscriptionMatchedStatus { get; }
```

ファイル先頭に `using ROSettaDDS.Dds;` (構造体を使うため) を追加。`Guid` は `using Guid = ROSettaDDS.Common.Guid;` 経由。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 失敗（`ReliableUserReader` / `BestEffortUserReader` が未実装のため）。

- [ ] **Step 3: コミット (この段階ではビルド失敗のまま保留可)**

Task 6, 7 で続けて実装するため、ここではコミットしない。

---

### Task 6: ReliableUserReader に status 実装

**Files:**
- Modify: `src/rosettadds/Dds/ReliableUserReader.cs`

- [ ] **Step 1: プロパティを追加**

`MatchedWriterCount` の直後に `SubscriptionMatchedStatus` を追加:

```csharp
    public int MatchedWriterCount => _reader.MatchedWriters.Count;

    public SubscriptionMatchedStatus SubscriptionMatchedStatus => _reader.SubscriptionMatchedStatus;
```

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: まだ失敗（`BestEffortUserReader` が未実装のため）。

---

### Task 7: BestEffortUserReader に status 実装

**Files:**
- Modify: `src/rosettadds/Dds/BestEffortUserReader.cs`

- [ ] **Step 1: 2 プロパティを追加**

`Start()` メソッドの前に以下を追加:

```csharp
    public int MatchedWriterCount => _reader.MatchedWriterCount;

    public SubscriptionMatchedStatus SubscriptionMatchedStatus => _reader.SubscriptionMatchedStatus;
```

`StatelessReader` には `MatchedWriterCount` プロパティを Task 4 で追加済み。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Dds/IUserReader.cs src/rosettadds/Dds/ReliableUserReader.cs src/rosettadds/Dds/BestEffortUserReader.cs
git commit -m "feat(dds): IUserReader に SubscriptionMatchedStatus と MatchedWriterCount を追加"
```

---

## Phase 2: WaitForMatchedAsync

### Task 8: MatchWaiter 内部ヘルパ

**Files:**
- Create: `src/rosettadds/Dds/MatchWaiter.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/rosettadds.Tests/Dds/MatchWaiterTests.cs` を新規作成:

```csharp
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class MatchWaiterTests
{
    [Fact]
    public void minCount_0_は即_true_を返す()
    {
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => 0, minCount: 0, timeout: TimeSpan.FromSeconds(1))
            .GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void 即時達成済みなら_true()
    {
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => 5, minCount: 3, timeout: TimeSpan.FromMilliseconds(100))
            .GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void タイムアウトで_false()
    {
        int counter = 0;
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => counter, minCount: 1, timeout: TimeSpan.FromMilliseconds(50))
            .GetAwaiter().GetResult();
        Assert.False(result);
    }

    [Fact]
    public void ポーリング_中に_達成したら_true()
    {
        int counter = 0;
        var task = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 3,
            timeout: TimeSpan.FromSeconds(2));
        // 50ms 後にカウンタを上げる
        Task.Run(async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref counter);
            await Task.Delay(20);
            Interlocked.Increment(ref counter);
            await Task.Delay(20);
            Interlocked.Increment(ref counter);
        });
        bool result = task.GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void 事前キャンセルで_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            MatchWaiter.WaitUntilMatchedAsync(
                () => 0, minCount: 1, timeout: TimeSpan.FromSeconds(1),
                cancellationToken: cts.Token)
                .GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void 待機中のキャンセルで_OperationCanceledException()
    {
        int counter = 0;
        using var cts = new CancellationTokenSource();
        var task = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 1,
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);
        // 50ms 後にキャンセル
        Task.Run(async () =>
        {
            await Task.Delay(50);
            cts.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
    }
}
```

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter MatchWaiterTests`
Expected: FAIL（`MatchWaiter` 型未定義）。

- [ ] **Step 3: 最小実装**

`src/rosettadds/Dds/MatchWaiter.cs` を新規作成:

```csharp
namespace ROSettaDDS.Dds;

/// <summary>
/// matched 数 polling 用の内部ヘルパ。
/// </summary>
internal static class MatchWaiter
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    public static async Task<bool> WaitUntilMatchedAsync(
        Func<int> currentCountAccessor,
        int minCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (currentCountAccessor is null) throw new ArgumentNullException(nameof(currentCountAccessor));
        if (minCount <= 0) return true;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        cancellationToken.ThrowIfCancellationRequested();

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (currentCountAccessor() >= minCount)
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(DefaultPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}
```

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test --filter MatchWaiterTests`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Dds/MatchWaiter.cs tests/rosettadds.Tests/Dds/MatchWaiterTests.cs
git commit -m "feat(dds): MatchWaiter 内部ヘルパと単体テストを追加"
```

---

### Task 9: Publisher<T> に status と WaitForMatchedAsync を追加

**Files:**
- Modify: `src/rosettadds/Dds/Publisher.cs`

- [ ] **Step 1: プロパティを追加**

`Publisher<T>` クラス内、`Writer` プロパティの直後に追加:

```csharp
    /// <summary>Publication マッチ状態 (Fast DDS 互換)。</summary>
    public PublicationMatchedStatus PublicationMatchedStatus => _writer.PublicationMatchedStatus;
```

- [ ] **Step 2: WaitForMatchedAsync を追加**

`Start()` メソッドの前に追加:

```csharp
    /// <summary>
    /// matched reader 数が <paramref name="minCount"/> に達するまで待機する。
    /// 戻り値: true=達成 / false=タイムアウト。
    /// </summary>
    public Task<bool> WaitForMatchedAsync(
        int minCount, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return MatchWaiter.WaitUntilMatchedAsync(
            () => _writer.MatchedReaders.Count,
            minCount, timeout, cancellationToken);
    }
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 4: コミット**

```bash
git add src/rosettadds/Dds/Publisher.cs
git commit -m "feat(dds): Publisher に PublicationMatchedStatus と WaitForMatchedAsync を追加"
```

---

### Task 10: Subscription<T> に status と WaitForMatchedAsync を追加

**Files:**
- Modify: `src/rosettadds/Dds/Subscription.cs`

- [ ] **Step 1: プロパティを追加**

`Subscription<T>` クラス内、`ReaderEntityId` プロパティの直後に追加:

```csharp
    /// <summary>Subscription マッチ状態 (Fast DDS 互換)。</summary>
    public SubscriptionMatchedStatus SubscriptionMatchedStatus => _reader.SubscriptionMatchedStatus;
```

- [ ] **Step 2: WaitForMatchedAsync を追加**

`DeserializeWithEncapsulation` メソッドの前に追加:

```csharp
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
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 4: 既存テストが緑のままか確認**

Run: `dotnet test --filter "Dds|Integration|Rtps"`
Expected: PASS（既存 API には破壊的変更なし）。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Dds/Subscription.cs
git commit -m "feat(dds): Subscription に SubscriptionMatchedStatus と WaitForMatchedAsync を追加"
```

---

## Phase 3: 結合テスト

### Task 11: 結合テスト追加

**Files:**
- Create: `tests/rosettadds.Tests/Integration/PublisherSubscriptionMatchedTests.cs`

- [ ] **Step 1: テストファイル作成**

`tests/rosettadds.Tests/Integration/SedpLoopbackTests.cs` の `CreatePair()` パターンを参考にしつつ、topic / typeName を揃える。

```csharp
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Integration;

public class PublisherSubscriptionMatchedTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(3);

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
    public async Task Publisher_PublicationMatchedStatus_CurrentCount_が_マッチ後に_1_になる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await pub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "Publisher 側で reader のマッチがタイムアウト");

        var status = pub.PublicationMatchedStatus;
        Assert.Equal(1, status.CurrentCount);
        Assert.Equal(1, status.TotalCount);
        Assert.NotNull(status.LastSubscriptionHandle);
        Assert.Equal(sub.Guid, status.LastSubscriptionHandle!.Value);
    }

    [Fact]
    public async Task Subscription_SubscriptionMatchedStatus_CurrentCount_が_マッチ後に_1_になる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "match_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await sub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "Subscription 側で writer のマッチがタイムアウト");

        var status = sub.SubscriptionMatchedStatus;
        Assert.Equal(1, status.CurrentCount);
        Assert.Equal(1, status.TotalCount);
        Assert.NotNull(status.LastPublicationHandle);
        Assert.Equal(pub.Guid, status.LastPublicationHandle!.Value);
    }

    [Fact]
    public async Task TotalCountChange_と_CurrentCountChange_は_read_でリセットされる()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "reset_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "reset_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, DiscoveryTimeout);

        var first = pub.PublicationMatchedStatus;
        Assert.Equal(1, first.CurrentCountChange);
        Assert.Equal(1, first.TotalCountChange);

        // 変化なしで再 read すると change は 0
        var second = pub.PublicationMatchedStatus;
        Assert.Equal(0, second.CurrentCountChange);
        Assert.Equal(0, second.TotalCountChange);
        Assert.Equal(first.CurrentCount, second.CurrentCount);
        Assert.Equal(first.TotalCount, second.TotalCount);
    }

    [Fact]
    public async Task BestEffort_subscription_でも_MatchedWriterCount_が_機能する()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "be_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName,
            reliability: ReliabilityQos.BestEffort);
        using var pub = pA.CreatePublisher<StringMessage>(
            "be_topic", StringMessageSerializer.Instance,
            reliability: ReliabilityQos.BestEffort,
            durability: DurabilityQos.Volatile,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();

        bool matched = await sub.WaitForMatchedAsync(1, DiscoveryTimeout);
        Assert.True(matched, "BestEffort subscription 側でマッチがタイムアウト");
        Assert.Equal(1, sub.SubscriptionMatchedStatus.CurrentCount);
    }

    [Fact]
    public async Task 既に達成済みなら_WaitForMatchedAsync_は即_true()
    {
        var env = CreatePair();
        using var pA = env.ParticipantA;
        using var pB = env.ParticipantB;

        using var sub = pB.CreateSubscription<StringMessage>(
            "fast_topic", StringMessageSerializer.Instance,
            (_, _) => { },
            typeName: StringMessage.DdsTypeName);
        using var pub = pA.CreatePublisher<StringMessage>(
            "fast_topic", StringMessageSerializer.Instance,
            typeName: StringMessage.DdsTypeName);

        pA.Start();
        pB.Start();
        await pub.WaitForMatchedAsync(1, DiscoveryTimeout);

        // 既にマッチ済みなので即 true
        bool matched = await pub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(1));
        Assert.True(matched);
    }
}
```

`using ROSettaDDS.Dds.QoS;` を追加する必要がある（`ReliabilityQos` / `DurabilityQos` のため）。

- [ ] **Step 2: テスト実行**

Run: `dotnet test --filter PublisherSubscriptionMatchedTests`
Expected: PASS

- [ ] **Step 3: コミット**

```bash
git add tests/rosettadds.Tests/Integration/PublisherSubscriptionMatchedTests.cs
git commit -m "test: Publisher/Subscription の MatchedStatus と WaitForMatchedAsync 結合テストを追加"
```

---

## Phase 4: perf テスト書き換え

### Task 12: perf テストの `DiscoveryDb` 直覗きを削除

**Files:**
- Modify: `Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs`

- [ ] **Step 1: ヘルパ関数を追加**

`WaitUntil` メソッドの直後に追加:

```csharp
    private static IEnumerator WaitForPublisherMatched(
        Publisher<StringMessage> publisher, int minCount, TimeSpan timeout)
    {
        var task = publisher.WaitForMatchedAsync(minCount, timeout);
        while (!task.IsCompleted)
        {
            yield return null;
        }
        Assert.IsTrue(task.Result,
            $"Publisher did not reach matched count {minCount} within {timeout}");
    }

    private static IEnumerator WaitForSubscriptionMatched(
        Subscription<StringMessage> subscription, int minCount, TimeSpan timeout)
    {
        var task = subscription.WaitForMatchedAsync(minCount, timeout);
        while (!task.IsCompleted)
        {
            yield return null;
        }
        Assert.IsTrue(task.Result,
            $"Subscription did not reach matched count {minCount} within {timeout}");
    }
```

- [ ] **Step 2: UnityToRos2 シナリオの待機を置換**

`RunUnityToRos2` 内:

```csharp
                // 旧:
                // yield return WaitForRemoteReader(participant, TopicName(topic), TimeSpan.FromSeconds(10));
                // 新:
                yield return WaitForPublisherMatched(publisher, 1, TimeSpan.FromSeconds(10));
```

- [ ] **Step 3: Ros2ToUnity シナリオの待機を置換**

`RunRos2ToUnity` 内:

```csharp
                // 旧:
                // yield return WaitForRemoteWriter(participant, TopicName(topic), TimeSpan.FromSeconds(10));
                // 新:
                yield return WaitForSubscriptionMatched(subscription, 1, TimeSpan.FromSeconds(10));
```

- [ ] **Step 4: `TopicName` ヘルパと `WaitForRemote{Reader,Writer}` を削除**

`private static string TopicName(string topic)` メソッドを削除。

`WaitForRemoteReader` / `WaitForRemoteWriter` メソッドを削除。

- [ ] **Step 5: 不要 using の掃除**

`DiscoveryDb` / `WriterSnapshot` / `ReaderSnapshot` への参照が消えたことを確認し、不要な `using ROSettaDDS.Discovery;` があれば削除する（本ファイルでは元から無いか確認の上で残す）。

- [ ] **Step 6: ビルド確認**

Run: `dotnet build Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs`（Unity プロジェクトのため Ros2Unity ソリューション経由の方が確実なら `dotnet build` を skip。Cs ファイル単体コンパイルは不可。代わりに `dotnet build src/rosettadds/rosettadds.csproj` のみ実行する）

Expected: 該当 Unity test ファイルは Ros2Unity ソリューションに含まれないため、メインの `dotnet build` は影響を受けない。

- [ ] **Step 7: コミット**

```bash
git add Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs
git commit -m "perf: DiscoveryDb 直覗きを WaitForMatchedAsync に置換"
```

---

## Phase 5: 全体検証

### Task 13: 全体ビルド・テスト

- [ ] **Step 1: ビルド**

Run: `dotnet build rosettadds.sln`
Expected: 成功（警告ゼロが望ましい）。

- [ ] **Step 2: 全テスト実行**

Run: `dotnet test`
Expected: 全て PASS。

- [ ] **Step 3: Unity meta ファイル整合性確認**

Run: `./.github/scripts/check_unity_meta.sh`
Expected: エラー無し。

- [ ] **Step 4: コミットは不要（最終確認）**

このタスクで修正が必要になったら別コミットで対処。

---

## 補足

- **派生課題 (issue #83 内)**: Unity→ROS2 publish の同期ループ (`PublishAsync().GetAwaiter().GetResult()`) は本 PR のスコープ外。別 issue で対処。
- **Listener API (`on_publication_matched` 相当)**: 本 PR のスコープ外。将来スペックとして Status 構造体の露出で hook 注入が容易になる基盤を整える。
- **`LastPublicationHandle` の Lifetime**: unmatch しても最後の writer GUID は保持。購読者が完全にいなくなっても値は残る仕様 (Fast DDS 互換)。
