# ROS 2 Service 対応 — クライアント先行 設計

- 日付: 2026-06-17
- ステータス: ドラフト（ユーザーレビュー前）
- 対象: ROSettaDDS に ROS 2 サービスのクライアント機能を追加する

## 背景と目的

ROSettaDDS は現在 pub/sub のみをサポートする。ROS 2 のサービス（request/reply）に
対応させたい。最終的にはクライアント・サーバ両対応を目指すが、Unity/.NET から
ROS 2 のサービスサーバを呼び出すユースケースが最も一般的なため、**まずクライアントを
実装する**。サーバは本設計で整える wire 層（inline QoS 相関・`.srv` 生成）を再利用する
後続スペックとする。

互換対象は **Fast DDS (rmw_fastrtps_cpp) のみ**。README / `docs/compatibility.md` の
既存方針と一致する。

## スコープ

- **本スペック**: Service Client + `.srv` コード生成 + Fast DDS 相関の read 経路
- **本スペック外（後続）**: Service Server、Cyclone DDS 互換、Action、Parameter

## ROS 2 サービスの wire 仕様（Fast DDS）

サービスは 2 つのトピックで構成される。

| 役割 | DDS トピック名 | DDS 型名 |
| --- | --- | --- |
| request | `rq/<service>Request` | `<pkg>::srv::dds_::<Service>_Request_` |
| reply | `rr/<service>Reply` | `<pkg>::srv::dds_::<Service>_Response_` |

`<service>` は ROS 2 サービス名の先頭 `/` を除いたもの（例 `add_two_ints`）。

### request / reply の相関

rmw_fastrtps はリクエストとレスポンスを **`SampleIdentity` による相関**で対応付ける。

- **request 送信時**: クライアントの request DataWriter が採番する RTPS サンプルの
  `SampleIdentity`（= request-writer GUID + writerSN）が、そのまま相関キーになる。
  request 側に特別な inline QoS は不要（サーバが RTPS サンプル情報から読む）。
- **reply 送信時**: サーバは reply DATA の inline QoS に `related_sample_identity` を載せる。
  - PID: **`0x800f` (`PID_CUSTOM_RELATED_SAMPLE_IDENTITY`)** を書く。
    読み取りはレガシ **`0x0083` (`PID_RELATED_SAMPLE_IDENTITY`)** もフォールバック受理する。
  - 値: `SampleIdentity` = GUID(16B: GuidPrefix 12B + EntityId 4B) + SequenceNumber(8B: high int32 + low uint32)。
- **クライアント突合**: 送信した request の `(request-writer GUID, writerSN)` を記録し、
  reply の `related_sample_identity` と一致するものを対応レスポンスとして解決する。

### QoS

ROS 2 services 既定プロファイルに合わせる: **Reliable / Volatile / KeepLast(depth 10)**。
request writer・reply reader ともに Reliable。

## アーキテクチャ

### 1. `.srv` コード生成（`ROSettaDDS.MsgGen` 拡張）

- **`SrvParser` を新設**: `.srv` 本文を `---` 区切りで request / response に分割し、
  各半分を既存 `MsgParser` のフィールド解析ロジックで解析する。出力は 2 つの
  `MessageDefinition`（名前 `<Service>_Request` / `<Service>_Response`）。
  - 共有ロジックを `MsgParser` から本文解析関数として切り出し、`SrvParser` と共用する。
- **`MessageDefinition` をサブ名前空間対応に**: 現状 `RosTypeName` が `/msg/` 固定。
  `SubNamespace`（`"msg"` / `"srv"`）を追加し、`RosTypeName`・出力パス・DDS 型名が
  それを反映する。既存 `.msg` 経路は `"msg"` 既定で挙動不変。
- **サービス記述子の生成**: `.srv` 1 件につき、Request/Response の struct + Serializer に
  加えて `ServiceDescriptor<TRequest, TResponse>` を生成する。記述子は以下を内包する。
  - サービス DDS 型名（Request/Response 双方）
  - Request/Response の `ICdrSerializer<T>`
  - これにより API 呼び出し時の型名・シリアライザ手動指定ミスを防ぐ。
- **genmsg CLI / SourceGenerator**: `<pkg>/srv/<Name>.srv` レイアウトを走査対象に追加。

### 2. Service wire 層（`ROSettaDDS.Dds` / `ROSettaDDS.Rcl` / `ROSettaDDS.Cdr`）

- **`TopicNameMangler`**: service request/reply 用の mangle を追加
  （`rq/<name>Request`、`rr/<name>Reply`）。既存 prefix 定数を利用。
- **`SampleIdentity` 型**: GUID + SequenceNumber を保持する値型を `Common` に追加。
  PL_CDR inline QoS への read/write（PID `0x800f` 書き込み / `0x800f`・`0x0083` 読み取り）を
  `ParameterId` 定数追加とともに実装。
- **inline QoS 受け渡し**:
  - reply reader が inline QoS をハンドラへ渡せるよう、`IUserReader.PayloadReceived`
    （payload + 送信元 + inlineQos）の低レベル経路をサービスクライアントから利用する。
    `Subscription<T>` の `(T, GuidPrefix)` シグネチャでは inline QoS が見えないため、
    サービスクライアントは reader を直接購読する。
  - request writer が **publish したサンプルの writerSN を取得**できるよう、
    writer/publish 経路に SN 返却を追加する。

### 3. クライアント API（`ROSettaDDS.Dds`）

```csharp
// 記述子は .srv から生成される
var client = participant.CreateServiceClient(
    AddTwoIntsService.Descriptor,   // ServiceDescriptor<Request, Response>
    "add_two_ints");

bool ready = await client.WaitForServiceAsync(TimeSpan.FromSeconds(5));
AddTwoIntsResponse resp = await client.CallAsync(
    new AddTwoIntsRequest(a: 2, b: 3),
    timeout: TimeSpan.FromSeconds(3),
    cancellationToken: ct);
```

- **`ServiceClient<TRequest, TResponse>`** を `DomainParticipant.CreateServiceClient` で生成。
  内部に request 用 `StatefulWriter`（rq トピック）と reply 用 reader（rr トピック）を持つ。
- **`CallAsync`**: request を publish → 採番された `SampleIdentity` をキーに
  `TaskCompletionSource<TResponse>` を保留マップへ登録 → reply 到着時に
  `related_sample_identity` で突合して解決。タイムアウト / `CancellationToken` で保留を解除。
- **`WaitForServiceAsync`**: `DiscoveryDb` 上で対応するサービスサーバの
  rq reader と rr writer の両方がマッチするまで待つ。
- スレッド安全: 保留マップは並行アクセスを想定しロックで保護。

## エラー処理

- `CallAsync` のタイムアウト / キャンセル時は保留エントリを除去し、
  `TimeoutException` / `OperationCanceledException` を送出。
- 未知 PID の inline QoS は既存方針どおりスキップ（must-understand でなければ無視）。
- reply の related identity が保留中のどれにも一致しない場合はログに記録して破棄
  （遅延到着 / 重複）。

## テスト

- **単体**
  - `SrvParser`: request/response 分割、空セクション、コメント、定数。
  - service トピック / 型名 mangle。
  - `SampleIdentity` の inline QoS read/write ラウンドトリップ（`0x800f` 書き / `0x0083` 読みフォールバック）。
  - 保留マップの突合・タイムアウト・キャンセル。
- **結合（ループバック）**
  - 自前 reply スタブ（rr writer + related_sample_identity 付与）と `ServiceClient` で
    request→reply 相関を end-to-end 検証。
- **interop（手動）**
  - 実 ROS 2 (Fast DDS) の `example_interfaces/AddTwoInts` および `std_srvs` サーバへの
    呼び出し手順を `docs/interop.md` に追記。

## 段階的実装方針

1. `.srv` パース + 生成器拡張（記述子含む）と単体テスト。
2. `SampleIdentity` + inline QoS read/write と単体テスト。
3. writer の writerSN 返却・reader の inlineQos 経路の拡張。
4. `ServiceClient` 本体 + ループバック結合テスト。
5. interop 手動検証 + ドキュメント更新。

各段階でビルド・テストを通してからコミットする。
