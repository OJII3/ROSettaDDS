# UserEndpointManager テスタビリティ向上リファクタ

## 背景

`UserEndpointManager` (`src/rosettadds/Dds/UserEndpointManager.cs`, 411 行) は、local user endpoint の登録状態とマッチングを担う中心的なコンポーネントである。しかし内部に次の詳細が残っており、EditMode / .NET unit test で挙動を絞りにくい。

- `lock (_lock)` を直接保持し、`_writers` / `_readers` / `_writersByTopic` / `_readersByTopic` / `_writerSnapshot` を直接 mutate する
- `ParticipantRtpsReceiver` への `RegisterWriter` / `UnregisterWriter` / `RegisterReader` / `UnregisterReader` を直接呼び出す
- 3 つの `Match` オーバーロード (LocalWriter×RemoteReader / LocalReader×RemoteWriter / LocalReader×LocalWriter) を private method として持ち、Type / QoS 判定と locator 解決と副作用 (`_logger.Debug` 出力) を同一メソッドに同居させている
- `ResolveRemoteEndpointUnicastLocator` が `DiscoveryDb.Snapshot()` を直接 walk して participant default locator にフォールバックする
- `shouldAdvertise` 判定 (同一 topic に同一 GUID を持つ endpoint が他にも残っているか) が `RemoveByReference` + `ContainsGuid` の 2 段呼び出しで表現されている

`DomainParticipant` テスタビリティ向上 (`docs/superpowers/specs/2026-06-30-domain-participant-testability-design.md`) で `ParticipantEndpointFactory` / `SedpEndpointAdvertiser` / `LeaseExpiryMonitor` への分離が完了しており、`UserEndpointManager` にも同じパターン (interface 抽出 + 純関数ヘルパー + 副作用分離) を適用したい。

## 目標

`UserEndpointManager` の責務を **状態 holder / 純ロジック / 副作用境界** の 3 つに分け、public API を一切変更せずに contract 単位でテスト可能にする。

## 設計

### 全体アーキテクチャ

| コンポーネント | 責務 | 種類 |
|---|---|---|
| `EndpointRegistry` | local writer/reader の登録状態管理 (lock + topic maps + writer snapshot) | 状態 holder |
| `EndpointMatcher` | match / unmatch 判定 + remote unicast locator 解決 + 純関数ヘルパー | 純ロジック |
| `IEndpointReceiver` + `ParticipantRtpsReceiverAdapter` | `ParticipantRtpsReceiver` への register / unregister 呼び出しを隠蔽 | 副作用境界 |
| `UserEndpointManager` (改修) | 上記 3 つを束ねるオーケストレータ。public API は据え置き | オーケストレータ |

### EndpointRegistry

`UserEndpointManager` 内部の lock 付きトピックマップと writer snapshot を、状態 holder として独立させる。

```csharp
internal sealed class EndpointRegistry
{
    private readonly object _lock = new();
    private readonly List<DiscoveredEndpointData> _writers = new();
    private readonly List<DiscoveredEndpointData> _readers = new();
    private readonly Dictionary<string, List<LocalWriter>> _writersByTopic = new();
    private readonly Dictionary<string, List<LocalReader>> _readersByTopic = new();
    private StatefulWriter[] _writerSnapshot = Array.Empty<StatefulWriter>();

    public void AddLocalWriter(DiscoveredEndpointData endpoint, LocalWriter writer);
    public void AddLocalReader(DiscoveredEndpointData endpoint, LocalReader reader);
    public LocalReader[] RemoveLocalWriter(Guid endpointGuid, StatefulWriter writer);
    public LocalWriter[] RemoveLocalReader(Guid endpointGuid, IUserReader reader);
    public bool ShouldAdvertiseForTopic(string topicName, Guid removedEndpointGuid);
    public LocalWriter[] GetLocalWritersForTopic(string topicName);
    public LocalReader[] GetLocalReadersForTopic(string topicName);
    public void StartWriters();
    public void StopWriters();
    public EndpointSnapshot Snapshot();
}
```

**ポイント:**
- `LocalWriter` / `LocalReader` record は `UserEndpointManager` の外でも使えるよう `internal` record として別ファイル (`LocalWriter.cs` / `LocalReader.cs`) に切り出す
- `RemoveLocalWriter` / `RemoveLocalReader` は「マッチした他方の endpoint 一覧 (同一 topic の他端)」も返す。元の `UnregisterWriter` / `UnregisterReader` の「lock 外で行う unmatch」を registry の lock 内で行うことで、`UserEndpointManager` 側の責務を「receiver 操作 + matcher 呼び出し」のみに絞る
- `RemoveLocalWriter` / `RemoveLocalReader` の戻り値は「マッチした他端の配列 (`LocalReader[]` / `LocalWriter[]`)」のみ。`shouldAdvertise` 判定 (同一 topic に同一 GUID を持つ endpoint が他にも残っているか) は別 API `bool ShouldAdvertiseForTopic(string topicName, Guid removedEndpointGuid)` として公開し、`UserEndpointManager` 側で `RemoveLocalWriter` 呼び出し後に続けて呼ぶ
- `UnregisterResult` 構造体 (`UserEndpointManager.cs:407`) の組み立ては `UserEndpointManager` 側で行う (`EndpointRegistry` からは返さない)
- `_writerSnapshot` の再構築 (`RefreshWriterSnapshotLocked`) は registry 内に閉じる
- `AddLocalWriter` / `AddLocalReader` はロック内で `_writers.Add` + `AddByTopic` + `RefreshWriterSnapshotLocked` まで一気通貫で行う

### EndpointMatcher

Match 判定と remote locator 解決を、純ロジックの集合として抽出する。

```csharp
internal static class EndpointMatcher
{
    internal static MatchDecision EvaluateLocalRemote(LocalWriter local, RemoteEndpoint remote);
    internal static MatchDecision EvaluateLocalRemote(LocalReader local, RemoteEndpoint remote);
    internal static MatchDecision EvaluateLocalLocal(LocalReader reader, LocalWriter writer);

    internal static Locator? ResolveRemoteUnicastLocator(
        RemoteEndpoint remote,
        IReadOnlyList<RemoteParticipant> participants);

    internal static Locator? FirstUdpLocator(IEnumerable<Locator> locators);
    internal static bool TypeMatches(string local, string remote);
}

internal readonly record struct MatchDecision(
    bool Compatible,
    Locator? UnicastLocator,
    ReliabilityKind? ReliabilityKind);
```

**ポイント:**
- `Match` の 3 オーバーロードを `EvaluateLocalRemote(writer, remote)` / `EvaluateLocalRemote(reader, remote)` / `EvaluateLocalLocal(reader, writer)` に整理。`UserEndpointManager` 側がオーバーロード解決する手間を排除
- 不一致時は `Compatible=false` を返し、呼び出し側で `Unmatch*` を実行する形に統一
- `ResolveRemoteUnicastLocator` は participants リスト (`DiscoveryDb.Snapshot()` の結果) を引数で受けることで、`DiscoveryDb` への直接依存を切らない
- 副作用 (`_logger.Debug` のログ出力) は `UserEndpointManager` 側に残す (元と同じ出力)
- `MatchDecision` 構造体は `internal readonly record struct` として別ファイル (`MatchDecision.cs`)

### IEndpointReceiver と ParticipantRtpsReceiverAdapter

`ParticipantRtpsReceiver` への register / unregister 呼び出しを、interface で隠蔽する。

```csharp
internal interface IEndpointReceiver
{
    void RegisterWriter(EntityId writerEntityId, StatefulWriter writer);
    void UnregisterWriter(EntityId writerEntityId);
    void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler);
    void UnregisterReader(EntityId readerEntityId);
}
```

**実装:**
- `ParticipantRtpsReceiverAdapter` (新規) — 既存の `ParticipantRtpsReceiver` インスタンスをラップして `IEndpointReceiver` を提供
- `UserEndpointManager` コンストラクタは `ParticipantRtpsReceiver` ではなく `IEndpointReceiver` を受けるよう変更
- 既存の `UserEndpointManager` 呼び出し元 (`DomainParticipant`) はコンストラクタで adapter を渡すよう 1 行変更

**ポイント:**
- interface は `internal` (`DomainParticipant` パターンに揃える)
- `ParticipantRtpsReceiver` 自体は触らない (公開 API 不変)

### UserEndpointManager (改修後オーケストレータ)

public API は完全に据え置き、内部実装を 3 コンポーネントへ委譲する形に変更する。

```csharp
internal sealed class UserEndpointManager
{
    private readonly DiscoveryDb _discoveryDb;
    private readonly IEndpointReceiver _receiver;
    private readonly EndpointRegistry _registry = new();
    private readonly ILogger _logger;

    public UserEndpointManager(
        DiscoveryDb discoveryDb,
        IEndpointReceiver receiver,
        ILogger logger);

    // public API は現状維持
    public void RegisterWriter(DiscoveredEndpointData endpointData, StatefulWriter writer);
    public void RegisterReader(DiscoveredEndpointData endpointData, IUserReader reader);
    public UnregisterResult UnregisterWriter(Guid endpointGuid, StatefulWriter writer);
    public UnregisterResult UnregisterReader(Guid endpointGuid, IUserReader reader);
    public EndpointSnapshot Snapshot();
    public void StartWriters();
    public void StopWriters();
    public void RemoteReaderChanged(RemoteEndpoint remoteReader);
    public void RemoteWriterChanged(RemoteEndpoint remoteWriter);
    public void RemoteReaderLost(RemoteEndpoint remoteReader);
    public void RemoteWriterLost(RemoteEndpoint remoteWriter);
}
```

**RegisterWriter の改修後フロー (例):**
```
1. EndpointMatcher.TypeMatches 相当の validate  (ValidateEndpoint 内に残す)
2. local = EndpointRegistry.AddLocalWriter(...)   // 状態遷移 (lock 内)
3. IEndpointReceiver.RegisterWriter(...)          // 副作用
4. foreach localReader in EndpointRegistry.GetLocalReadersForTopic(topicName):
     TryMatchLocalRemote(localReader, local)       // EndpointMatcher.EvaluateLocalRemote
5. foreach remoteReader in discoveryDb.ReaderSnapshot():
     if remoteReader.TopicName == topicName:
       TryMatchLocalRemote(local, remoteReader)
```

**ポイント:**
- `Unregister*` の `shouldAdvertise` 判定は `EndpointRegistry.ShouldAdvertiseForTopic(topicName, endpointGuid)` を `RemoveLocalWriter/Reader` の直後に呼び出して取得する (元の `RemoveByReference` + `ContainsGuid` 判定を内部に持つ)
- `Remote*Changed` / `Remote*Lost` も registry から該当 topic の endpoint を取得し、matcher で判定して `Match*` / `Unmatch*` を呼ぶだけ
- ロガー呼び出し (`_logger.Debug`) は orchestrator に残す (元と同じ出力)
- `EndpointSnapshot` / `UnregisterResult` の record は `UserEndpointManager.cs` 内に残す (公開 surface の維持)

## 非目標

- `UserEndpointManager` の public API の変更 (signature / 振る舞いとも不変)
- `DomainParticipant` の public API の変更
- DDS / RTPS wire protocol の変更
- `ParticipantRtpsReceiver` の interface 化や実装変更 (adapter で wrap するのみ)
- `DiscoveryDb` の interface 化
- `BestEffortUserReader` / `ReliableUserReader` / `StatefulWriter` のリファクタ
- `IUserReader` interface の変更
- 別経路やフォールバック実装の追加
- 既存テストファイル `UserEndpointManagerTests.cs` の削除 (据え置き)

## テスト方針 (TDD)

DomainParticipant パターンに揃え、テストを先に書き、その後コンポーネントを実装する。

**新規テストファイル (`tests/rosettadds.Tests/Dds/` 配下):**

| ファイル | テスト対象 | 主要ケース |
|---|---|---|
| `EndpointRegistryTests.cs` | `EndpointRegistry` | Add/Remove 後の snapshot 整合、lock 内 writer snapshot 再構築、topic 別 isolate、Remove 時の他端戻り値、`shouldAdvertise` 判定 |
| `EndpointMatcherTests.cs` | `EndpointMatcher` | LocalRemote 互換性 (type 一致 / 不一致、QoS 互換 / 非互換)、LocalLocal 互換性、FirstUdpLocator 4 種 (UDPv4/v6 / 無し / 空)、TypeMatches 4 種、ResolveRemoteUnicastLocator 3 経路 (endpoint 直指定 / participant default / 解決失敗) |
| `UserEndpointManagerRefactoredTests.cs` | `UserEndpointManager` (`IEndpointReceiver` を fake 化) | Register/Unregister の正常系、Remote*Changed / Remote*Lost の match/unmatch 伝播、shouldAdvertise 判定、not found 時の `UnregisterResult.NotFound`、validate 失敗時の例外 |
| `FakeEndpointReceiver.cs` | fake impl | `IEndpointReceiver` を実装し、register/unregister の呼び出し履歴を記録 |

**DomainParticipant パターン同様:**
- 既存 integration test (`UserEndpointManagerTests.cs` 2 件 + DDS 統合テスト) が緑のまま通ることを受け入れ基準とする
- 既存テストファイル `UserEndpointManagerTests.cs` の 2 件はそのまま残し、新規テストで厚みを追加

**Unity .meta ファイル:**
- `src/rosettadds/Dds/EndpointRegistry.cs` 等の新規ファイルに対応する `.meta` を追加
- `.github/scripts/check_unity_meta.sh` がクリーン

## 影響範囲

**新規ファイル (`src/rosettadds/Dds/` 配下):**
- `EndpointRegistry.cs` (+ `.meta`)
- `EndpointMatcher.cs` (+ `.meta`)
- `LocalWriter.cs` (+ `.meta`) — `record` を別ファイルに
- `LocalReader.cs` (+ `.meta`) — `record` を別ファイルに
- `IEndpointReceiver.cs` (+ `.meta`)
- `ParticipantRtpsReceiverAdapter.cs` (+ `.meta`)
- `MatchDecision.cs` (+ `.meta`)

**変更ファイル:**
- `src/rosettadds/Dds/UserEndpointManager.cs` — 内部実装を上記 3 コンポーネントへ委譲
- `src/rosettadds/Dds/DomainParticipant.cs` — `_userEndpoints` コンストラクタ呼び出しを `new ParticipantRtpsReceiverAdapter(_receiver)` 経由に変更 (1 行)
- `tests/rosettadds.Tests/Dds/UserEndpointManagerTests.cs` — 既存 2 件は据え置き (新規テストで厚みを追加)

**新規テストファイル (`tests/rosettadds.Tests/Dds/` 配下):**
- `EndpointRegistryTests.cs`
- `EndpointMatcherTests.cs`
- `UserEndpointManagerRefactoredTests.cs`
- `FakeEndpointReceiver.cs`

## 設計上の注意点

- **後方互換**: `UserEndpointManager` の public API は signature / 振る舞いとも完全不変。`DomainParticipant` 側の呼び出しは adapter を 1 段挟むだけ。
- **`MatchDecision` 構造体の出力互換**: `Match` の 3 ケース (compatible + locator / not compatible) を `MatchDecision` で表現する。呼び出し側 (`UserEndpointManager`) が `Compatible=false` のとき `Unmatch*` を呼び、`Compatible=true` のとき `Match*` + `_logger.Debug` 出力を行う。デバッグログのメッセージは元と完全一致。
- **`RemoveLocalWriter` / `RemoveLocalReader` の戻り値**: 「マッチした他端の配列」と「`shouldAdvertise` フラグ」をまとめて返すと API が太るため、`EndpointRegistry` 側で分離する。`RemoveLocalWriter` は `LocalReader[]` を返し、`shouldAdvertise` は `EndpointRegistry` 内の別 API `bool ShouldAdvertiseForTopic(string topicName, Guid removedEndpointGuid)` で取得する。`UserEndpointManager.UnregisterWriter` / `UnregisterReader` はこの 2 つを順に呼んで `UnregisterResult` を組み立てる。
- **lock のスコープ**: `EndpointRegistry` 内の lock は state 遷移の直撃保護に留め、receiver への register / unregister 呼び出しは lock 外で行う (元と同じ)。
- **reader snapshot の存在**: 現状 `EndpointRegistry._writerSnapshot` は存在するが `_readerSnapshot` は存在しない。`UserEndpointManager.Snapshot()` は LINQ で都度生成している。`UserEndpointManager` の public API として `Snapshot()` が返すべき型 (`EndpointSnapshot`) とその生成コストは変えない。
- **`_writerSnapshot` の thread safety**: 現状 `Volatile.Read(ref _writerSnapshot)` を使っているが、改修後は `EndpointRegistry` 内で同等の `Volatile.Read` を使う。`StartWriters` / `StopWriters` も `Volatile.Read` 経由で取得する。
- **既存 `IUserReader` interface の利用**: 改修後も `IUserReader` 自体は触らず、`UserEndpointManager.RegisterReader` / `UnregisterReader` の signature も据え置き。

## 受け入れ基準

- [ ] `EndpointRegistry` の unit test が緑
- [ ] `EndpointMatcher` の unit test が緑
- [ ] `UserEndpointManager` (IEndpointReceiver fake 注入) の unit test が緑
- [ ] 既存 `UserEndpointManagerTests.cs` の 2 件が緑のまま
- [ ] 既存 DDS 統合テスト (`tests/rosettadds.Tests/Integration/`) が緑のまま
- [ ] `UserEndpointManager` の public API への破壊的変更なし
- [ ] `DomainParticipant` の public API への破壊的変更なし
- [ ] `ParticipantRtpsReceiver` の interface 化なし (adapter 経由のみ)
- [ ] `check_unity_meta.sh` クリーン
