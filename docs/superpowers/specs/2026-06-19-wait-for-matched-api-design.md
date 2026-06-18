# WaitForMatched API — Publisher/Subscription にマッチ待ちを追加

- 日付: 2026-06-19
- ステータス: ドラフト（ユーザーレビュー待ち）
- 対象: issue #83「ROSettaDDS 本体に matched 待ち API を追加 (DiscoveryDb 直接覗きの置き換え)」
- ブランチ: `feat/wait-for-matched-api`

## 背景と目的

perf 計測テスト (`ROSettaDDSUnityRos2PerfTests`) や一般ユーザコードが endpoint の
マッチ完了を待つ際、現状は `participant.DiscoveryDb.{Reader,Writer}Snapshot()` を
直接 polling し、DDS topic 名 (`rt/...`) を手で組み立てている。

```csharp
// 現状のワークアラウンド (perf テスト)
private static string TopicName(string topic) => "rt/" + topic.TrimStart('/');
private static IEnumerator WaitForRemoteWriter(DomainParticipant participant, string ddsTopic, TimeSpan timeout)
    => WaitUntil(() => participant.DiscoveryDb.WriterSnapshot().Any(ep => ep.TopicName == ddsTopic), timeout);
```

これは内部表現 (`DiscoveryDb`, mangle 済み topic 名) をユーザに露出させるワーク
アラウンドで、過去に `rt//foo` 二重スラッシュバグの温床になった。

ROSettaDDS 本体に DDS 標準 / Fast DDS 相当のマッチ API を追加し、内部表現を
ユーザから隠蔽する。

## ゴール

`Publisher<T>` / `Subscription<T>` から以下を呼べるようにする:

1. **マッチ状態 snapshot** を取得
2. **マッチ数達成まで非同期待機** (timeout / キャンセル対応)

perf テストは snapshot を直接覗く / DDS topic 名を組み立てることをやめて、
新しい API だけを使う。

## スコープ

- **本スペック**: `PublicationMatchedStatus` / `SubscriptionMatchedStatus` 構造体、
  `Publisher<T>.PublicationMatchedStatus` / `Subscription<T>.SubscriptionMatchedStatus`
  プロパティ、`WaitForMatchedAsync(int minCount, TimeSpan timeout, CancellationToken)`
  待機 API、BestEffort 経路の `MatchedWriterCount` 整備、perf テスト書き換え
- **本スペック外 (将来)**: リスナー API (`on_publication_matched` /
  `on_subscription_matched` 相当)、match/unmatch イベントフック
- **本スペック外 (別 issue)**: 派生課題の `PublishAsync().GetAwaiter().GetResult()`
  同期ループ改善

## Fast DDS 仕様 (参考)

調査対象: `eProsima/Fast-DDS v3.6.1` の
`include/fastdds/dds/core/status/{MatchedStatus,PublicationMatchedStatus,SubscriptionMatchedStatus}.hpp`
および `DataWriter.hpp` / `DataWriterListener.hpp` / `DataReaderListener.hpp`。

DDS 標準にも Fast DDS にも `wait_for_matched` 相当は存在しない。`get_*_matched_status`
を polling するか listener を使うのが標準パターン。これに揃える。

### Status 構造体 (Fast DDS 互換)

```cpp
struct MatchedStatus {
    int32_t total_count;
    int32_t total_count_change;   // 前回 read からの差分 (read で 0 にリセット)
    int32_t current_count;
    int32_t current_count_change; // 同上
};
struct PublicationMatchedStatus : MatchedStatus {
    InstanceHandle_t last_subscription_handle;
};
struct SubscriptionMatchedStatus : MatchedStatus {
    InstanceHandle_t last_publication_handle;
};
```

`total_count` は unmatch しても減らない累計値、`current_count` は現在マッチ数の
スナップショット。DDS 仕様準拠。

## アーキテクチャ

### 1. Status 構造体 (新規)

`src/rosettadds/Dds/MatchedStatus.cs` を新規作成し、以下を置く:

```csharp
namespace ROSettaDDS.Dds;

/// <summary>
/// Publication マッチ状態 (Fast DDS PublicationMatchedStatus 互換)。
/// <see cref="Publisher{T}.PublicationMatchedStatus"/> から取得する。
/// </summary>
public readonly struct PublicationMatchedStatus
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }
    public Guid? LastSubscriptionHandle { get; init; }
}

/// <summary>
/// Subscription マッチ状態 (Fast DDS SubscriptionMatchedStatus 互換)。
/// <see cref="Subscription{T}.SubscriptionMatchedStatus"/> から取得する。
/// </summary>
public readonly struct SubscriptionMatchedStatus
{
    public int TotalCount { get; init; }
    public int TotalCountChange { get; init; }
    public int CurrentCount { get; init; }
    public int CurrentCountChange { get; init; }
    public Guid? LastPublicationHandle { get; init; }
}
```

`init` setter を採用し、`new PublicationMatchedStatus { ... }` で生成可能にする
（Fast DDS の out param スタイルと一致）。

### 2. StatefulWriter / StatefulReader / StatelessReader に TotalCount 追加

既存の `MatchedReaders` / `MatchedWriters` に加えて、累計マッチ数を管理する:

```csharp
// StatefulWriter
private long _totalMatchedReaders;       // lifetime 累計
private int _lastReportedCurrentReaders;  // status read 時のスナップショット
private long _lastReportedTotalReaders;

public PublicationMatchedStatus PublicationMatchedStatus
{
    get
    {
        int current;
        long total;
        lock (_matchedLock)
        {
            current = _matched.Count;
            total = _totalMatchedReaders;
        }
        return new PublicationMatchedStatus
        {
            CurrentCount = current,
            CurrentCountChange = current - _lastReportedCurrentReaders,
            TotalCount = checked((int)total),
            TotalCountChange = checked((int)(total - _lastReportedTotalReaders)),
            LastSubscriptionHandle = /* 最後にマッチした reader guid */,
        };
        // _lastReported* 更新は read 時にやる
    }
}
```

- `MatchReader` で新規追加時に `_totalMatchedReaders++`
- `UnmatchReader` では current のみ減り、total は維持
- `LastSubscriptionHandle` は `MatchReader` の呼び出しで更新 (新規マッチした guid)
- `total_count` は単調増加 (uint64 相当が安全だが int64 で十分)

**差分リセット仕様**: Fast DDS は `*_count_change` を「status を read すると
次の read までは 0 になる」ようにリセットする。同様に read 時に
`_lastReported*` を更新する。

### 3. `Publisher<T>.PublicationMatchedStatus` プロパティ

`Publisher<T>` 経由で `StatefulWriter` の status を委譲公開:

```csharp
public PublicationMatchedStatus PublicationMatchedStatus => _writer.PublicationMatchedStatus;
```

### 4. `Subscription<T>.SubscriptionMatchedStatus` プロパティ

`Subscription<T>` 内の `IUserReader` から status を取得するため、`IUserReader`
に `MatchedWriters` snapshot 相当を追加する。実装 2 種 (Reliable /
BestEffort) 両方に `MatchedWriterCount` 相当の API を持たせる。

#### IUserReader 拡張

```csharp
internal interface IUserReader : IDisposable
{
    // ... 既存 ...
    /// <summary>matched writer の現在数。</summary>
    int MatchedWriterCount { get; }
    /// <summary>matched writer の累計数 (lifetime)。</summary>
    long TotalMatchedWriters { get; }
    /// <summary>subscription matched status snapshot (Fast DDS 互換)。</summary>
    SubscriptionMatchedStatus SubscriptionMatchedStatus { get; }
    /// <summary>最後にマッチした writer guid (ある場合)。</summary>
    Guid? LastMatchedWriterHandle { get; }
}
```

`ReliableUserReader` / `BestEffortUserReader` の各実装で、対応する
`StatefulReader` / `StatelessReader` の値を集約。`StatefulReader` 側に
`TotalMatchedWriters` と `LastMatchedWriterHandle` を新規追加 (上の
`StatefulWriter` と同じ仕組み)。

`StatelessReader` 側にも同じ変更を入れる。`LastMatchedWriterHandle` は
`MatchWriter` で更新、`UnmatchWriter` では変えない (「最後にマッチした」を
保持)。

### 5. `Subscription<T>.SubscriptionMatchedStatus` プロパティ

```csharp
public SubscriptionMatchedStatus SubscriptionMatchedStatus
{
    get
    {
        var s = _reader.SubscriptionMatchedStatus;
        return s;
    }
}
```

### 6. `WaitForMatchedAsync` 待機 API

Publisher/Subscription 双方に追加。実装は両者でほぼ対称なので、
internal な静的ヘルパに切り出す:

```csharp
// Publisher<T> / Subscription<T> 共通ヘルパ (例: src/rosettadds/Dds/MatchWaiter.cs)
internal static class MatchWaiter
{
    public static async Task<bool> WaitUntilMatchedAsync(
        Func<int> currentCountAccessor,
        int minCount,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        if (minCount <= 0) return true;
        cancellationToken.ThrowIfCancellationRequested();

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (currentCountAccessor() >= minCount) return true;
            if (DateTime.UtcNow >= deadline) return false;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;  // 外側 ct のキャンセルを伝搬
            }
        }
    }
}
```

#### Publisher<T>

```csharp
public Task<bool> WaitForMatchedAsync(
    int minCount, TimeSpan timeout, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    return MatchWaiter.WaitUntilMatchedAsync(
        () => _writer.MatchedReaders.Count,
        minCount, timeout, cancellationToken);
}
```

#### Subscription<T>

```csharp
public Task<bool> WaitForMatchedAsync(
    int minCount, TimeSpan timeout, CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    return MatchWaiter.WaitUntilMatchedAsync(
        () => _reader.MatchedWriterCount,
        minCount, timeout, cancellationToken);
}
```

#### 動作
- 既に `currentCount >= minCount` なら即 `true` を返す
- 20ms 間隔で polling (DDS 既定の discovery interval 100ms 程度より十分短い)
- timeout 到達で `false`
- キャンセルは `OperationCanceledException`
- `minCount <= 0` は即 `true` (防御的)
- `timeout < TimeSpan.Zero` は `ArgumentOutOfRangeException`

### 7. perf テスト書き換え

`Ros2Unity/Assets/Tests/Ros2Perf/ROSettaDDSUnityRos2PerfTests.cs` から以下を削除:
- `TopicName(string)` ヘルパ
- `WaitForRemoteReader(...)` / `WaitForRemoteWriter(...)` ヘルパ

置換 (Unity Test の `IEnumerator` との接続用):

```csharp
// 既存 WaitUntil(Func<bool>, TimeSpan) の上に WaitFor を追加
private static IEnumerator WaitForMatched(Publisher<StringMessage> publisher, int minCount, TimeSpan timeout)
{
    var task = publisher.WaitForMatchedAsync(minCount, timeout);
    while (!task.IsCompleted)
    {
        yield return null;
    }
    Assert.IsTrue(task.Result, $"Publisher did not reach matched count {minCount} within {timeout}");
}
```

呼び出し側 (UnityToRos2 / Ros2ToUnity シナリオ):
- `yield return WaitForRemoteReader(participant, TopicName(topic), 10s);`
  → `yield return WaitForMatched(publisher, 1, TimeSpan.FromSeconds(10));`
- 同様に writer 側を `subscription.WaitForMatchedAsync(1, 10s)` で置換

`DiscoveryDb` の `ReaderSnapshot` / `WriterSnapshot` 利用を本ファイルから削除
(他テストの整合性は維持)。

## テスト

### 単体テスト (`tests/rosettadds.Tests/Dds/`)

新規: `MatchedStatusTests.cs`
- `PublicationMatchedStatus` / `SubscriptionMatchedStatus` の init-only setter 動作
- (構造体なので record 的な使い方の smoke test)

`StatefulWriter` / `StatefulReader` / `StatelessReader` のステータス動作は
既存の `tests/rosettadds.Tests/Rtps/` 配下の統合テストに追加。

新規: `MatchWaiterTests.cs`
- 即 true: `minCount=0` / 既に達成済み
- タイムアウトで false
- 途中で達成したら true
- キャンセルで `OperationCanceledException`

### 結合テスト (`tests/rosettadds.Tests/Integration/`)

新規: `PublisherSubscriptionMatchedTests.cs` (loopback hub)
- シナリオ 1: Publisher 1 つ + Subscriber 1 つ
  - pA の Publisher 作成 → pB の Subscription 作成 →
    `publisher.PublicationMatchedStatus.CurrentCount == 0` →
    双方 Start → 一定時間内に `CurrentCount == 1` →
    publisher.WaitForMatchedAsync(1, 5s) == true
- シナリオ 2: Subscription 側対称
  - pB の Subscription 作成 → pA の Publisher 作成 → 同様に
    `subscription.SubscriptionMatchedStatus.CurrentCount` を検証
- シナリオ 3: total_count の単調増加
  - リモート participant を落として再参加させ、
    `TotalCount >= 2` / `CurrentCount` の動きを確認
- シナリオ 4: BestEffort
  - BestEffort subscription で `MatchedWriterCount` が機能することを検証

### perf テスト
既存テスト `ROSettaDDSUnityRos2PerfTests` を本スペックに従って書き換え。
Unity 環境での実行が必要なため、CI での自動実行は対象外。
ローカル / 手動での回帰確認 + `nix develop` でのヘルパ build 手順は既存どおり。

## 段階的実装方針

1. `IUserReader` 拡張 + `BestEffortUserReader` / `ReliableUserReader` 実装
   (BestEffort 経路の `MatchedWriterCount` 追加も含む)
2. `StatefulReader` / `StatelessReader` に累計マッチ数と status プロパティ追加
3. `StatefulWriter` に累計マッチ数と status プロパティ追加
4. `MatchedStatus.cs` 構造体 + `MatchWaiter.cs` 内部ヘルパ
5. `Publisher<T>` / `Subscription<T>` に status プロパティと `WaitForMatchedAsync` 追加
6. 単体 / 結合テスト追加
7. perf テスト書き換え

## 互換性 / リスク

- **API 追加のみ**。既存コード (`MatchedReaderCount` / `MatchedWriterCount` 直参照
  など) には変更なし。`Publisher.Writer` / `Subscription` 内部構造の private
  拡張のみ。
- **Status 構造体**: `init` setter なので将来フィールド追加も後方互換。
- **`MatchReader` の戻り値**: 既存 void 維持。`MatchReader` の中で累計値更新
  だけ追加。
- **Polling オーバーヘッド**: 20ms 間隔 × 待機中の `Task.Delay`。通常待機は
  数百 ms 〜 数秒で完了するため問題なし。
- **dispose 中の挙動**: `ThrowIfDisposed` を待機前に呼び、待機中の dispose は
  検出しない (Race)。既存 `Publisher<T>` / `Subscription<T>` と同じ前提。

## 参考リンク

- issue: https://github.com/OJII3/ROSettaDDS/issues/83
- Fast DDS `PublicationMatchedStatus.hpp`: <https://github.com/eProsima/Fast-DDS/blob/v3.6.1/include/fastdds/dds/core/status/PublicationMatchedStatus.hpp>
- Fast DDS `SubscriptionMatchedStatus.hpp`: <https://github.com/eProsima/Fast-DDS/blob/v3.6.1/include/fastdds/dds/core/status/SubscriptionMatchedStatus.hpp>
- Fast DDS `MatchedStatus.hpp`: <https://github.com/eProsima/Fast-DDS/blob/v3.6.1/include/fastdds/dds/core/status/MatchedStatus.hpp>
- Fast DDS `DataWriterListener.hpp`: <https://github.com/eProsima/Fast-DDS/blob/v3.6.1/include/fastdds/dds/publisher/DataWriterListener.hpp>
- Fast DDS `DataReaderListener.hpp`: <https://github.com/eProsima/Fast-DDS/blob/v3.6.1/include/fastdds/dds/subscriber/DataReaderListener.hpp>
