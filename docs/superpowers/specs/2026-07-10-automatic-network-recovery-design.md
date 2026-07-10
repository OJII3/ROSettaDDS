# Wi-Fi 再接続時の自動ネットワーク復旧 設計

## 背景

ROSettaDDS は `Context` 生成時に UDP socket を作成し、multicast group へ一度だけ join する。Android 端末で Wi-Fi を OFF→ON、またはローミングすると既存 socket の multicast membership が無効になる場合があるが、現在は socket を再作成しない。その結果、既存 participant は lease 中だけ残り、lease 切れ後に SEDP endpoint と topic が消える。アプリ再起動で復旧するのは `Context` と socket が作り直されるためである。

## 目標

- ネットワーク変更後、既存の `Context`、`Node`、publisher、subscription、service client を破棄せず通信を自動復旧する。
- 組み込み UDP transport の socket と multicast membership を再作成する。
- 変更後の NIC と locator を SPDP/SEDP で再広告する。
- 通常利用では設定を要求せず、自動復旧を明示的に無効化できるようにする。

## 対象外

- custom transport の再作成または所有権管理。
- Android Java/Kotlin API や UnityEngine への依存追加。
- ネットワーク断中の user sample の永続キューイング。
- `Context`、`Node`、publisher、subscription の公開インスタンス差し替え。

## 採用方式

`UdpTransport` のインスタンスを維持したまま、内部 socket を停止・破棄・再作成する。SPDP/SEDP/RTPS reader・writer は既存の `IRtpsTransport` 参照を保持でき、受信イベントの購読も維持される。

`Context` は `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` を購読する。この API は netstandard で提供され、NIC の IP アドレス変更を通知する。通知は短時間に複数回発生し得るため、1 秒デバウンスして単一の復旧処理へ集約する。

transport 代理オブジェクトを追加する方式は、同一 `UdpTransport` の内部を再初期化すれば固定参照を維持できるため採用しない。DDS グラフ全体の再構築は、公開 endpoint インスタンスと履歴・マッチ状態の移植が必要となり、変更範囲が大きいため採用しない。

## 公開 API

`ContextOptions` に次を追加する。

```csharp
public bool EnableAutomaticNetworkRecovery { get; init; } = true;
```

既定は有効とする。`false` の場合、`Context` はネットワーク変更通知を購読しない。custom transport は復旧時に再作成・停止・破棄しない。組み込み transport と custom transport が混在する場合は、組み込み transport だけを再作成する。

デバウンス時間は1秒、最大試行回数は3回、再試行間隔は500ミリ秒の実装定数とし、公開設定にはしない。今回の障害対応に不要な設定面を増やさない。

## コンポーネント

### ネットワーク変更通知

本番実装は `NetworkChange.NetworkAddressChanged` を購読する。テストでは実ネットワークを変更せず決定的に通知できるよう、内部インターフェースを `Context` へ注入できる内部コンストラクタを設ける。公開コンストラクタは本番実装を使用する。

通知コールバックでは socket 操作を直接行わず、キャンセル可能な非同期デバウンス処理を開始する。次の通知が来た場合は先の待機をキャンセルし、最後の通知から1秒後に復旧を開始する。

### `UdpTransport`

生成時の構成を保持し、同じ unicast/multicast種別、bind address、port、join interface、TTL、受信バッファ設定で socket を再作成できるようにする。

restart は次の順で行う。

1. 送受信との競合を防ぐ排他を取得する。
2. restart 前に動作中だったかを記録する。
3. 受信・dispatch loop を停止する。
4. 旧 socket を閉じる。
5. 新 socket を生成して bind する。
6. multicast transport は group へ再joinし、送信インターフェースとTTLを再設定する。
7. restart 前に動作中だった場合は受信・dispatch loop を再開する。

`UdpTransport` 自体と `Received` event は同一インスタンスのまま維持する。restart中に始まったsendは同じ排他に入り、新socketが利用可能になってから送信する。

### `ParticipantTransportSet`

4本の transport のうち、自身が所有する `UdpTransport` だけをrestartする。borrowしたcustom transportには操作しない。

restart後、`LocalhostOnly` と `LocalUnicastAddress` の規則を再適用し、既定構成では `LocalNetwork.EnumerateUnicastIPv4()` から広告アドレスを再列挙する。metatraffic/user unicast locatorを更新し、現在の `ResolvedParticipantId` とbind portを維持する。

### `Context` と endpoint 再広告

`Context` は復旧処理を直列化し、同時に複数のrestartを実行しない。処理中はSPDP/SEDP writerを一時停止し、socket再作成後に再開する。transport set全体の `Stop()` は呼ばず、各owned `UdpTransport` が自分の受信・dispatch loopだけを一時停止する。これにより混在するcustom transportの `Start()` / `Stop()` / `Dispose()` は呼ばれない。

SPDPの `BuildParticipantData()` は更新済みtransport locatorを参照する。復旧後は周期送信を待たず1回即時送信する。

既存の `DiscoveredEndpointData` は生成時のlocatorを保持しているため、各 `Node` のローカルendpoint registryからwriter/readerの発見データをsnapshotする。unicast/multicast locatorを更新し、それぞれ `SedpEndpointWriter.AddEndpointAsync` へ再投入する。同一endpoint GUIDの旧履歴は既存実装どおり最新状態へ置換される。

## データフロー

1. OSがNICのIP変更を通知する。
2. `Context` が通知を受け、1秒デバウンスする。
3. Context単位の復旧ロックを取得する。
4. SPDP/SEDP送信とowned UDP transportの受信を一時停止する。
5. owned UDP socketを閉じ、同一設定・ポートで再作成する。
6. multicast socketを再joinする。
7. 現在のNICから広告locatorを再計算する。
8. transportとdiscovery writerを再開する。
9. 既存endpointのlocatorを更新しSEDPで再広告する。
10. SPDPを即時送信し、通常周期処理へ戻る。

## エラー処理

- 1回の復旧が失敗した場合、500ミリ秒置いて最大3回試行する。
- 各失敗は試行回数と例外をloggerへ記録する。
- 3回失敗しても `Context` と公開endpointは破棄しない。次のネットワーク変更通知で再試行できる状態を保つ。
- `Context.Dispose()` は通知購読を解除し、保留中のデバウンス・再試行をキャンセルして完了を待つ。
- Dispose開始後に到着した通知は無視する。
- restart中のsendはsocket差し替え用の排他により、新socketの準備後に実行する。復旧不能時の送信例外は既存各送信ループのlogger処理へ渡す。

## テスト

### `UdpTransport`

- restart後も同じインスタンスで送受信できる。
- restart前のsocketを閉じ、unicast portを再bindできる。
- multicast restart後にgroupへ再joinし受信を再開できる。
- Start/Stop/Dispose/restartが安全に直列化され、Dispose後のrestartは失敗する。

### `ParticipantTransportSet`

- owned UDP transportだけをrestartする。
- custom transportをrestart・disposeしない。
- restart後にNIC由来のlocatorを更新する。
- Start前とStart後のrestartで状態を維持する。

### `Context`

- `EnableAutomaticNetworkRecovery` の既定値が `true` である。
- `false` の場合は通知を購読しない。
- 連続通知を1回の復旧へ集約する。
- Dispose後の通知を無視する。
- 一時失敗時に最大3回再試行する。
- 復旧後にSPDPと全ローカルSEDP endpointを再広告する。
- 既存 `Node`、publisher、subscriptionを破棄しない。

### 回帰確認

- 対象テストをTDDのRED→GREENで実行する。
- 全.NETテストを実行する。
- `.github/scripts/check_unity_meta.sh` で `src/rosettadds` の `.meta` 不足とorphanを確認する。
- Unity Editor検証がUPM/DNS障害で実行不能な場合は、ソース由来の失敗と混同せずPRへ明記する。

## 完了条件

- 自動復旧が既定で有効であり、明示的に無効化できる。
- Wi-Fi再接続相当の通知後、同じ公開オブジェクトを使ってUDP multicast discoveryとuser data通信を再開できる。
- 新しいNIC locatorがSPDP/SEDPへ反映される。
- custom transportの所有権とライフサイクルを変更しない。
- 対象テスト、全.NETテスト、Unity meta検査が通る。実機Quest検証は利用可能な環境で別途行う。
