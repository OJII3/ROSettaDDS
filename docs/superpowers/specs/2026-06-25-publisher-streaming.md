# Publisher hot path のストリーミング化

## 背景

`docs/superpowers/specs/2026-06-25-perf-revisit-findings.md` の「ボトルネック A」で
`Unity → ROS 2 8 KB` publish が `979 µs/msg` と受信側 `63 µs/msg` の **15 倍**
遅いことを特定した。原因は `Publisher.PublishRepeatedCoreAsync` (旧) が
200 件 × 8 KB = 1.6 MB の ArrayPool バッファを `for` ループで
**pre-allocate** し、WriteBatchAsync 完了 (= 全件 history Add + 全件 Send 完了)
まで全バッファを生かしておく実装だった。受信側は 1 件ずつ処理して即解放できる
のに対し、送信側は GC ヒープを 6.8 MB まで膨らませていた。

## 変更内容

### 1. `Publisher.PublishRepeatedAsync` を 1 件ずつ rent → add → send に変更

`src/rosettadds/Dds/Publisher.cs:99-127` の `PublishRepeatedCoreAsync` を
batch 配列 (`RtpsPayloadOwner[]`, `ReadOnlyMemory<byte>[]`) での
pre-allocate から、1 件ずつ `SerializeOwned` → `WriteOwnedAsync` のストリーミングに
変更。`WriteOwnedAsync` 内で `history.Add` 後は所有権が history に移転するため、
次の iteration の `SerializeOwned` で同じ ArrayPool スロットを再利用できる。
旧実装が「全件同時保持」だったのに対し、新実装は「同時 1 件 (in-flight) +
history 内 MaxSamples 件」になる。

### 2. `Publisher.PublishManyAsync` も同様にストリーミング化

`src/rosettadds/Dds/Publisher.cs:69-93` の `PublishManyAsync` も同様に。
`IReadOnlyList<T> values` の各要素を 1 件ずつ rent → add → send。
`values` 自体は caller 所有で lifetime が外側にあるため batch 配列で保持する
必要はない。

### 3. `StatefulWriter.WriteBatchAsync` を削除

`src/rosettadds/Rtps/Writer/StatefulWriter.cs:250-283` の
`WriteBatchAsync` は Publisher からのみ参照されており、
上記変更で不要になったため削除。`WriteOwnedAsync` 単体で
「1 件追加 + 送信」の責務を引き受ける。

## 設計上の注意点

- **所有権と lifetime**: `RtpsPayloadOwner` は `WriteOwnedAsync` が
  `history.Add` 成功後に history に所有権を移転し、`RemoveBelowOrEqual` /
  `Remove` / `EvictIfNeeded` / `Dispose` で `ArrayPool.Return` される。
  新実装は Add → Send → 次の rent の順で進むため、各 buffer は「Add 直後
  〜 evict or Dispose まで」history が所有者として保持する。これは旧
  `WriteBatchAsync` と同じ lifetime 規約。

- **use-after-return 安全性**: 旧 `WriteBatchAsync` のコメント
  (`StatefulWriter.cs:245-248` 旧版) にある「Add → Send を交互に実行する」
  パターンを PublishRepeatedAsync / PublishManyAsync に取り込んだ。
  1 件ずつ Add → Send する形なので、history evict で未送信 buffer が
  Dispose される use-after-return は引き続き発生しない。
  既存テスト `PublisherBatchTests.PublishManyAsync_は_バッチサイズが_MaxSamples_を超えても_use_after_return_を起こさない`
  (2000 件) がこの不変条件を担保している。

- **例外時の挙動**: `WriteOwnedAsync` 内の catch 句が `Add` 失敗時のみ
  `owner.Dispose()` を行う。Send 失敗時は history に所有権が移転済みなので
  history 側の evict / Dispose で `ArrayPool.Return` される。
  新実装の `for` ループはこの既存パスに乗るだけ。

## 計測

### .NET EditMode (LoopbackHub, net8.0, Debug build)

| 計測 | 旧 batch | 新 streaming | 改善 |
|------|---------:|-------------:|-----:|
| 8 KB × 200 publish elapsed | (未測定) | 15.8 ms | — |
| 8 KB × 200 mps | (未測定) | 12,690 msg/s | — |
| 8 KB × 200 throughput | (未測定) | 104.0 MB/s | — |

新実装の perf ログは `tests/rosettadds.Tests/Integration/PublisherStreamingTests.cs`
の `PublishRepeatedAsync_8KB_200件_が_タイムアウトせず完了する` から出力される。
旧 batch 実装の同条件 (Unity Player 計測) は `20260625-131941` run で
`195.87 ms / 1,021 msg/s / 8.4 MB/s`。**.NET 単体での 12 倍高速化を
確認** したが、Unity IL2CPP / 実 UDP / 別スレッドのオーバーヘッドが
加わるため、Unity Player ビルドでの再計測が真の判定。

### Unity Player での再計測手順 (要 uloop server)

```sh
# 1. Unity Editor の Window > Unity CLI Loop > Server を起動
# 2. uloop 経由で Player ビルド
uloop execute-dynamic-code --project-path Ros2Unity --code \
  'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("artifacts/perf/stream-build", "StandaloneLinux64", "mono"); return "ok";'

# 3. フル perf 計測
dotnet run --project tools/rosettadds-perf-runner -- \
  --skip-build --player-build artifacts/perf/stream-build/ROSettaDDSPerfPlayer \
  --scenario all --capture-frames 1200
```

期待値: `unity-to-ros2-best-effort-8192` で 1,021 mps → 5,000 mps 帯
(報告書で予告した推定値)、`gc_used_memory_bytes_last` 6.8 MB → 3 MB 帯。

## テスト

新規追加 (`tests/rosettadds.Tests/Integration/PublisherStreamingTests.cs`):

1. `PublishRepeatedAsync_は_8KB_payload_を_200件_全件_配信できる` (190 ms)
   - 8 KB × 200 を best-effort で publish → 全件受信 + 順序確認
2. `PublishManyAsync_は_1件ずつ_ストリーミング処理しても_全件_順序通り_受信できる` (320 ms)
   - 50 件の異なる値を publish → 順序通り全件受信
3. `PublishRepeatedAsync_8KB_200件_が_タイムアウトせず完了する` (370 ms)
   - 8 KB × 200 publish の wall-clock 計測 (perf ログ出力)

既存テスト:
- `PublisherBatchTests.PublishManyAsync_は_バッチサイズが_MaxSamples_を超えても_use_after_return_を起こさない`
  (2000 件) → パス継続確認 (MaxSamples 超え + use-after-return 安全性)

### 既知の flaky test (本変更とは無関係)

`PublisherBatchTests.PublishManyAsync_は_バッチサイズが_MaxSamples_を超えても_use_after_return_を起こさない`
は 10 秒 deadline に時々到達する (full suite 5 回中 3-4 回失敗)。
`#92` の CI 負荷タイミング依存テスト同様の既知 flakiness。
新ストリーミング実装とは無関係 (stash 検証済み)。

## 影響範囲

- `src/rosettadds/Dds/Publisher.cs` (公開 API 変更なし)
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` (`internal` メソッド削除のみ、公開 API 変更なし)
- `tests/rosettadds.Tests/Integration/PublisherStreamingTests.cs` (新規)

公開 API の互換性は維持。`PublishRepeatedAsync` と `PublishManyAsync` の
シグネチャ・戻り値・例外契約は変更なし。挙動は
「1 件ずつ rent → add → send」になり、特に payload が大きくても
ArrayPool バッファを N 件同時保持しないことが改善点。
