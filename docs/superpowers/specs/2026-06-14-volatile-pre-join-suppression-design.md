# Volatile Stateful Writer 向け pre-join sample suppression 設計

日付: 2026-06-14
状態: 承認済み
関連 issue: https://github.com/OJII3/ROSettaDDS/issues/78

## 背景と課題

`PubSubLoopbackTests.late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない`
が CI で稀に失敗する (issue #78)。

`docs/compatibility.md` にも明記されているとおり、現状の rosettadds は
「Volatile writer が late-join reader に対し、join 前サンプルを再送してしまう」
レースを制御できていない:

> Volatile reader への late-join 抑止は未実装。Volatile publisher は新規サンプルを
> reader に積極再送しないが、Reliable reader が HEARTBEAT に対し未受信 SN を NACK した
> 場合、join 前のサンプルが再送され得る (GAP による pre-join sample の明示的除外は
> 今後の課題)。

CI のような遅延・負荷の高い環境では、SEDP roundtrip で match 成立が遅延し、
writer の HB 周期 (1s デフォルト) と重なりうるタイミングで reader が NACK を発行、
writer が join 前サンプルを DATA で再送、300ms のアサーション待ち時間内に reader に
届き `received = true` となりテストが FAIL する。ローカルでは roundtrip が短く、
300ms 内に HB → NACK → DATA 往復が入らないため安定している。

## ゴール / 非ゴール

### ゴール

- Volatile Stateful writer が late-join reliable reader に対し、pre-join サンプルを
  DATA で再送せず、GAP で「無関連」と通知する。
- `late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない` を CI でも
  決定的に成功させる。
- RTPS 仕様 (8.4.x) と Fast DDS (`rmw_fastrtps_cpp`) の挙動に整合させる。

### 非ゴール (本 issue スコープ外)

- TransientLocal writer の挙動変更 (現状の履歴再送を維持)。
- BestEffort reader の挙動変更 (そもそも NACK を発行しない)。
- Stateless writer 経路 (`StatelessWriter`) の変更 (そもそも ACKNACK を受信しない)。
- match 時の proactive GAP 送信 (案 B 相当。reader 初回 NACK を 1 RTT 節約する
  最適化だが、フレーキー解消とは独立。別 issue として切り出す)。
- `StatefulWriter` 以外の RTPS 実装 (`StatelessWriter` 等) への展開。

## 設計

### データモデル: `ReaderProxy` への low watermark 追加

`src/rosettadds/Rtps/Writer/ReaderProxy.cs` に以下を追加する。

- フィールド:
  - `private long _lowWatermark` (default `0`)
  - `private bool _lowWatermarkSet` (default `false`)
- パブリック API:
  - `bool IsLowWatermarkSet { get; }`
  - `SequenceNumber? LowWatermark { get; }` (未設定時 `null`)
  - `void SetLowWatermark(SequenceNumber sn)`: watermark を設定。
    既に設定済みの場合は呼び出しを無視 (idempotent、warning ログを出してもよい)。
  - `bool IsPreJoin(SequenceNumber sn)`: 設定済みかつ `sn.Value > 0` かつ
    `sn.Value <= _lowWatermark` のとき `true`。`sn.Value == 0` (未初期化) は
    `false` を返す (履歴空のときはそもそも比較対象がない)。

### 適用条件: `StatefulWriter.MatchReader`

`src/rosettadds/Rtps/Writer/StatefulWriter.cs` の `MatchReader` を変更する。

新規 proxy を `_matched` に追加した直後 (lock 外)、次の条件を満たす場合に
watermark を設定する:

- `!_resendHistoryOnMatch` … Volatile writer 相当。TransientLocal は除外。
- `reliability == ReliabilityKind.Reliable` … BestEffort は NACK しないので除外。
- `_history.LastSequenceNumber.Value > 0` … 履歴空のときは抑制対象なし。

具体的な呼び出し:

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
        var lastSn = _history.LastSequenceNumber;
        if (lastSn.Value > 0)
        {
            addedProxy.SetLowWatermark(lastSn);
            _logger.Debug(
                $"StatefulWriter: low watermark {lastSn} set for reader {readerGuid} (Volatile pre-join suppression)");
        }
    }
}
```

`RunBackground` は使わない。`SetLowWatermark` は in-memory 設定のみで、
外部 I/O や状態遷移を伴わない。

### NACK 処理: `StatefulWriter.ResendRequestedAsync`

`src/rosettadds/Rtps/Writer/StatefulWriter.cs` の `ResendRequestedAsync` を変更する。

各 `requested` SN について:

1. `proxy.IsPreJoin(sn)` が `true` の場合:
   - `SendGapToDestinationAsync(sn, proxy.ReaderGuid.EntityId, dest, ct)` を呼ぶ。
   - `proxy.ClearRequested(sn)`。
2. それ以外:
   - 既存ロジック通り `_history.Get(sn)` を見て DATA or GAP。

これにより、reader からの NACK (bitmap に pre-join SN が含まれる) に対し
writer は GAP を返し、reader は `WriterProxy.MarkGap` で該当範囲を
satisfy 済みにマークし、以後 NACK しなくなる。

### 既存 API 互換性

- `ReaderProxy` の既存パブリック API
  (`Reliability` / `IsReliable` / `HighestAcked` / `ProcessAckNack` /
  `RequestedSequenceNumbers` / `ClearRequested` / `IncrementHeartbeatCount` /
  `UpdateUnicastLocator` / `UnicastLocator`) は変更なし。
- `StatefulWriter` のパブリック API も変更なし。挙動のみが変わる。
- 内部挙動の差分は Volatile + Reliable match → pre-join NACK → GAP 応答のみ。
  TransientLocal / BestEffort / 履歴空 / 通常の再送ロジックは完全に維持される。

## テスト

### 既存テスト

- `late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない`:
  そのまま残す。本 fix 適用後は CI でも決定的に成功する。
- `late_Subscription_は_TRANSIENT_LOCAL_user_writer_の履歴を受信する`:
  TransientLocal 経路は本 fix の対象外 (regression 確認として CI で成功を維持)。
- `chatter_を_Publisher_から_Subscription_に届ける`,
  `複数件_publish_すると_順番に_受信`, `SEDP_で発見した_remote_writer_*` 等の
  通常ケースは影響を受けない (Volatile で pre-join 抑止が効く経路は今回のみ)。

### 新規テスト (任意・低優先)

直接 `StatefulWriter` を構築し、proxy の watermark が match 時に設定されることを
検証する unit test。統合テストの `late_Subscription_には_VOLATILE_user_writer_の
履歴を再送しない` で挙動は十分検証できるため、必要性を再評価してから追加する。

## ドキュメント更新

`docs/compatibility.md` の "QoS と history depth の現状" セクション末尾の引用
ブロック (現状):

> Volatile reader への late-join 抑止は未実装。Volatile publisher は新規サンプルを
> reader に積極再送しないが、Reliable reader が HEARTBEAT に対し未受信 SN を NACK
> した場合、join 前のサンプルが再送され得る
> (GAP による pre-join sample の明示的除外は今後の課題)。

を以下に書き換える:

> Volatile Stateful writer は、late-join reliable reader に対し、match 時点の
> lastSN を per-reader の low watermark として保持する。NACK されてきた pre-join
> サンプル (SN ≤ watermark) には GAP を返して「無関連」と通知し、DATA では再送
> しない。TransientLocal writer の履歴再送経路 (Volatile 用とは別の `resendHistoryOnMatch`
> 経路) はこの対象外。`PubSubLoopbackTests.late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない`
> が本挙動を担保する。

## 影響範囲 / リスク

- **変更ファイル**: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`,
  `src/rosettadds/Rtps/Writer/ReaderProxy.cs`, `docs/compatibility.md`。
  discovery / SEDP / transport / CDR 層には触れない。
- **互換性**:
  - 既存 reliable reader / TransientLocal 挙動は完全に維持。
  - 既存 reliable reader が pre-join サンプルを受け取ってしまうケース (フレーキー)
    は解消方向のみ。
  - RTPS 仕様 8.4.x および Fast DDS / Cyclone DDS と wire 互換
    (pre-join を GAP 通知する挙動は両 vendor とも仕様準拠)。
- **エッジケース**:
  - 履歴空 (writer 書き込み前に reader match) → watermark 未設定、抑制なし。
  - BestEffort reader → watermark 設定対象外、抑制なし。
  - TransientLocal writer → `_resendHistoryOnMatch == true` で watermark 設定対象外、
    既存通り履歴再送 (正)。
  - reader Unmatch → 再 Match → 新規 proxy 生成で watermark 再設定 (現行の
    `UnmatchReader` 動作と整合)。
  - match 時点で pre-join サンプルが複数ある場合 → 各 SN 個別に GAP 応答
    (現状の `ResendRequestedAsync` ループで順次処理)。

## 検証手順

1. `dotnet build` / `dotnet test` がすべて成功。
2. `late_Subscription_には_VOLATILE_user_writer_の履歴を再送しない` が CI で安定。
3. 関連テスト (`late_Subscription_は_TRANSIENT_LOCAL_user_writer_の履歴を受信する`,
   `chatter_を_Publisher_から_Subscription_に届ける`) が regression なく成功。

## 実装後のフォローアップ候補 (本 issue のスコープ外)

- match 時の proactive GAP 送信 (案 B): 1 RTT 削減の最適化。別 issue で検討。
- `LoopbackHub` ベースの packet capture を使った GAP 送信の低レベル検証テスト。
- `UserWriterHeartbeatPeriod` をテストで制御可能にする API 整備
  (フレーキー検証を CI でやりやすくする)。
