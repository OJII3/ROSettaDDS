# DomainParticipant テスタビリティ向上リファクタリング

## 背景

`DomainParticipant` は既に `ParticipantTransportSet` と `UserEndpointManager` に一部責務を分離している。
一方で、公開 API の内部に次の詳細が残っている。

- publisher / subscription / service reply reader の RTPS endpoint 生成
- `DiscoveredEndpointData` の構築と locator / QoS 設定
- SEDP の add / unregister 非同期処理と例外処理
- unregister の同期待ちと timeout ログ
- lease expiry loop の開始、停止、周期計算

これらはネットワークや discovery の副作用と密結合しており、失敗時の挙動や生成される endpoint data を
小さい単位で検証しにくい。

## 目標

`DomainParticipant` を DDS 公開 API とコンポーネントのライフサイクル統括に寄せる。
endpoint 生成、SEDP 広告、lease 監視を独立した内部コンポーネントへ分け、契約単位でテストできるようにする。

## 設計

### ParticipantEndpointFactory

`ParticipantEndpointFactory` は user endpoint の生成を担当する。

- writer entity id / reader entity id の採番
- `StatefulWriter` の生成
- reliable / best-effort reader の生成
- publication / subscription / service reply reader 用 `DiscoveredEndpointData` の生成
- default unicast locator と user multicast locator の設定
- 明示 type name がない場合の `DdsTypeName` 解決

`DomainParticipant` は factory の結果を `UserEndpointManager` と SEDP 広告へ渡すだけにする。

### SedpEndpointAdvertiser

`SedpEndpointAdvertiser` は SEDP endpoint writer への副作用を集約する。

- publication / subscription endpoint の add
- publication / subscription endpoint の unregister
- add 失敗時の warning ログ
- dispose / cancellation 済みの場合の例外抑制
- unregister timeout と失敗ログ

登録状態の変更は `UserEndpointManager` に残し、SEDP 広告の副作用だけを分離する。

### LeaseExpiryMonitor

`LeaseExpiryMonitor` は remote participant の lease expiry を定期実行する。

- `SpdpInterval` と `LeaseDuration` から check period を計算する
- start / stop を冪等にする
- stop 時に cancellation と短い待機を行う
- loop の例外は logger に記録する

`DiscoveryDb.ExpireOldParticipants(DateTime.UtcNow)` の呼び出しはこのクラスへ閉じ込める。

## 非目標

- 公開 API の変更
- DDS / RTPS wire protocol の変更
- QoS 対応範囲の拡張
- Discovery / RTPS reader / writer の再設計
- 後方互換用の別経路やフォールバック実装の追加

## テスト方針

TDD で進める。

- `ParticipantEndpointFactory` は endpoint data の topic / type / QoS / locator / entity kind を検証する。
- `SedpEndpointAdvertiser` は add / unregister の成功、失敗ログ、timeout を検証する。
- `LeaseExpiryMonitor` は period 計算と start / stop の冪等性を検証する。
- 既存 integration test で publisher / subscription / service client の挙動維持を確認する。
- Unity に取り込まれる `src/rosettadds` 配下の追加 `.cs` には `.meta` を追加し、meta 検査を実行する。
