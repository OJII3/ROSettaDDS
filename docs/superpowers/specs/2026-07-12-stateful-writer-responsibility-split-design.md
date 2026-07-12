# StatefulWriter 責務分割 設計書

## 1. 目的

`StatefulWriter` の密結合を解消し、保守性・障害リスク低減を実現する。公開API・RTPS wire出力・処理順序は完全に維持する。

## 2. 対象

- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` (793行, 33メソッド)

## 3. 責務分割

### 3.1 MatchedReaderRegistry

ReaderProxyの集合とPublicationMatchedStatusの管理を担当する内部クラス。

```csharp
internal sealed class MatchedReaderRegistry
{
    public (ReaderProxy Proxy, bool Added) Match(
        Guid readerGuid,
        Locator? locator,
        ReliabilityKind reliability);

    public void Unmatch(Guid readerGuid);
    public ReaderProxy? Find(Guid readerGuid);
    public ReaderProxy[] Snapshot();
    public int Count { get; }
    public PublicationMatchedStatus TakePublicationMatchedStatus();
    public SequenceNumber? MinimumReliableAcknowledged();
}
```

**責務:**
- Dictionary<Guid, ReaderProxy>とカウンターのロック管理
- Match/Unmatch/Find/Snapshot操作
- PublicationMatchedStatusのスナップショット取得とchange値リセット
- Reliable readerのみを対象とした最小ACK算出

### 3.2 StatefulWriterPacketSender

RTPS submessageの構築と送信を担当する内部クラス。

```csharp
internal sealed class StatefulWriterPacketSender
{
    public ValueTask SendDataAsync(
        CacheChange change,
        EntityId readerEntityId,
        Locator destination,
        CancellationToken cancellationToken);

    public ValueTask SendHeartbeatAsync(
        SequenceNumber first,
        SequenceNumber last,
        EntityId readerEntityId,
        Locator destination,
        int count,
        CancellationToken cancellationToken);

    public ValueTask SendGapAsync(
        SequenceNumber missingSequenceNumber,
        EntityId readerEntityId,
        Locator destination,
        CancellationToken cancellationToken);
}
```

**責務:**
- DATA/DATA_FRAG/HEARTBEAT/GAPのpacket構築
- IRtpsTransportへの送信
- ArrayPoolによるbuffer管理
- 送信例外のログと伝播
- Fragment送信の逐次処理

### 3.3 BackgroundOperationTracker

非同期タスクの追跡と終了待機を担当する内部クラス。

```csharp
internal sealed class BackgroundOperationTracker
{
    public void Run(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken);

    public void WaitForCompletion(TimeSpan timeout);
}
```

**責務:**
- TaskのHashSet管理とロック
- ContinueWithによる完了時自動削除
- OperationCanceledException/ObjectDisposedExceptionの正常終了扱い
- その他の例外はWarnレベルでログ
- WaitForCompletionでの全task待機

## 4. StatefulWriterに残る責務

- Heartbeat周期の開始/停止 (CTS管理)
- ACKNACK受信時の判断ロジック (pre-join判定、purge有効判定)
- Writerへの要求SNのルックアップ
- Historyへの所有権移譲 (WriteOwnedAsync)
- IRtpsSubmessageHandlerの実装
- 公開プロパティとメソッドのファサード

## 5. 処理フロー

### 通常送信
1. StatefulWriterがhistoryへ追加しSNを確定
2. MatchedReaderRegistryからSnapshotを取得
3. StatefulWriterPacketSenderへ順番に送信委譲

### ACKNACK受信
1. StatefulWriterが宛先と送信元を検証
2. ReaderProxy.ProcessAckNackを呼出
3. purge有効時はMinimumReliableAcknowledgedで算出
4. History.RemoveBelowOrEqualを実行
5. BackgroundOperationTrackerへ再送タスク登録

### Start/Stop
1. Start: CTS作成、Heartbeatループ起動
2. Stop: CTSキャンセル、Heartbeatループ待機、BackgroundOperationTracker.WaitForCompletion

## 6. エラー処理

- キャンセルされた送信例外: 呼び出し元へ伝播
- それ以外の送信例外: ログ記録し握り潰す
- Background taskのOperationCanceledException/ObjectDisposedException: 正常終了扱い
- それ以外のbackground例外: Warnレベルでログ
- OnPacketReceivedの解析失敗: Warnレベルでログ

## 7. 制約事項

- 公開APIシグネチャの変更なし
- RTPS wire出力の変更なし
- 新規のretry/timeout/例外型の追加なし
- payload lifetime問題は今回のスコープ外

## 8. 検証方針

- 抽出前のcharacterization testで既存挙動を固定
- 各抽出段階で対象テストと全.NETテストを実行
- bit-level packet比較でwire出力を保証
- netstandard2.1ビルドでUnity互換性を確認
