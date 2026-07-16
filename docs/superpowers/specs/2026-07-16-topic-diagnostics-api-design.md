# Topic Diagnostics API Design

## Goal

ROSettaDDS に ROS 2 の `ros2 topic list`、`topic info`、`topic hz` に相当する診断機能を、CLI ではなく再利用可能な C# API として追加する。
主な利用者は .NET アプリケーションと Unity であり、ROS 2 CLI の文字列出力互換は対象外とする。

## Scope

`ROSettaDDS.Rcl.Diagnostics` 名前空間に `TopicDiagnostics` を追加し、`Node.CreateTopicDiagnostics()` から取得できるようにする。

```csharp
using var diagnostics = node.CreateTopicDiagnostics();
var topics = diagnostics.GetTopics();
var info = diagnostics.GetTopicInfo("/chatter");
using var monitor = diagnostics.CreateFrequencyMonitor("/chatter");
```

### Topic listing and info

- `GetTopics()` は呼び出し時点のスナップショットを返す。
- 同じ `Context` 内の local endpoint と SEDP で発見した remote endpoint を GUID で集約する。
- 通常の `rt/` topic のみを対象とし、`rq/` と `rr/` の service 内部 topic は除外する。
- topic 名は `/chatter` 形式、型名は `std_msgs/msg/String` 形式へ demangle する。
- `TopicInfo` は topic 名、型名一覧、publisher/subscriber 数、endpoint 詳細を持つ。
- endpoint 詳細は GUID、種別、local/remote、型、Reliability、Durability など SEDP から取得可能な QoS を含む。
- node 名・namespace は現在の discovery metadata に存在しないため、推測して追加しない。
- 複数型が存在する topic は型名一覧にすべて残す。

### Frequency monitoring

`CreateFrequencyMonitor` は対象 topic の型情報を確認し、型をデシリアライズせず raw CDR payload の到着時刻だけを計測する一時 reader を作成する。

- 型未発見、または複数型の場合は暗黙に選択せず失敗する。
- 受信 callback では `Stopwatch.GetTimestamp()` のみを記録する。
- 固定サイズのリングバッファで直近 `WindowSize` 件を保持する。
- `TopicFrequencyStatistics` は `RateHz`、`MinInterval`、`MaxInterval`、`MeanInterval`、`StandardDeviation`、`SampleCount`、`WindowDuration` を返す。
- サンプル不足時は `HasData=false` とし、統計値を無効扱いにする。
- `WaitForMatchedAsync` を提供する。
- `Dispose` で reader、receiver 登録、SEDP 広告を解除する。
- 計測中は対象 topic の subscriber 数が一時的に 1 増えることを API ドキュメントに明記する。
- 初版では echo、payload サイズ、帯域幅、delay は対象外とする。

## Errors and lifecycle

- `GetTopicInfo` は未発見 topic に対して `null` を返す。
- monitor 作成時の未発見 topic、複数型、無効な引数は明示的な例外で通知する。
- `Context` または `Node` の破棄後の操作は既存 API と同様に `ObjectDisposedException` とする。
- snapshot は endpoint の追加・削除、remote endpoint の発見・消失を跨いで一貫した状態を返す。
- 同一 timestamp や短い計測窓を安全に扱い、負の間隔とゼロ除算を発生させない。

## Testing

既存の `tests/rosettadds.Tests` に以下を追加する。

- local/remote endpoint の topic 集約
- service topic の除外
- topic/type name の demangle
- endpoint の GUID、local/remote、QoS
- リングバッファと rate、min/max、標準偏差、空状態
- monitor の開始、停止、Dispose 後の endpoint 解除
- loopback payload の受信間隔計測

既存の ROS 2 Fast DDS interop 検証には、ROS 2 topic の発見と frequency monitor の計測ケースを追加する。

## Alternatives considered

1. `TopicDiagnostics` を `Node` から生成する。graph 参照と一時 reader の所有を分離し、`Node` を薄く保てるため採用する。
2. `Node` に診断メソッドを直接追加する。呼び出しは簡単だが、Node に診断責務が混在する。
3. raw graph と raw subscription だけを公開する。実装は小さいが、利用者が集約・demangle・統計を再実装することになる。
