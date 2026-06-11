# DomainParticipant 責務分離

## 背景

`DomainParticipant` は公開 API に加えて、次の責務を直接保持している。

- 4 種類の RTPS transport の生成、所有権判定、開始、停止、破棄
- local user endpoint の保持、スナップショット生成、packet dispatch
- local / remote user endpoint のマッチングと解除
- SPDP / SEDP の構築とライフサイクル管理
- lease expiry loop と非同期 SEDP 操作

この構造では、状態の不変条件と副作用の実行順序が 1 クラスに集中し、個別責務の契約を
検証しにくい。

## 目標

`DomainParticipant` を DDS 公開 API と各コンポーネントのオーケストレーションに限定する。

### ParticipantTransportSet

transport の生成と所有権をカプセル化し、次の契約を持つ。

- custom transport は借用し、破棄しない
- 内部生成した transport のみ所有し、破棄する
- 生成途中で失敗した場合、生成済みの所有 transport を破棄する
- 開始、停止、破棄の順序を一箇所で管理する
- participant が広告する locator は transport 構成から一意に決める

### UserEndpointManager

local user endpoint の状態とマッチングをカプセル化し、次の契約を持つ。

- endpoint の登録と解除は endpoint GUID と実体の組で扱う
- topic と DDS type name が一致する endpoint だけをマッチする
- remote discovery の通知に応じてマッチ状態を更新する
- packet dispatch は lock 内で行わず、不変なスナップショットへ配送する
- endpoint 登録状態の変更と SEDP 広告は分離し、SEDP 副作用は `DomainParticipant` が実行する

## 段階的な変更

1. transport の生成、所有権、ライフサイクルを `ParticipantTransportSet` へ抽出する。
2. local user endpoint の状態、マッチング、packet dispatch を `UserEndpointManager` へ抽出する。
3. `DomainParticipant` を新しい境界の利用側へ変更し、公開 API の振る舞いを維持する。
4. 契約単位のテストと既存 integration test、Unity meta 検査を実行する。

## 非目標

- DDS / RTPS wire protocol の変更
- 公開 API の互換レイヤー追加
- QoS 対応範囲の拡張
- SPDP / SEDP 実装自体の再設計
