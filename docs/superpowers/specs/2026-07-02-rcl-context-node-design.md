# Rcl.Context / Rcl.Node 分離設計

## 背景

`ROSettaDDS.Dds.DomainParticipant` は ROSettaDDS の公開エントリポイントであり、ユーザーが直接 `new` して `CreatePublisher` / `CreateSubscription` / `CreateServiceClient` を呼び出す。これは ROS 2 のメンタルモデルでいう `rcl_node_t` (rclcpp::Node) に相当するユーザー操作対象であると同時に、`rcl_context_t` に相当するドメイン共通の DDS 資源 (UDP transport × 4、DiscoveryDb、SPDP / SEDP) も内部に抱えている。

その結果として、現状の公開 API には次の DDS 感が出ている。

- クラス名が DDS 用語 (`DomainParticipant` / `DomainParticipantOptions`)。
- `GuidPrefix` / `Guid` / `DiscoveryDb` / `UserMulticastTransport` / `UserUnicastTransport` / `UserMulticastDestination` など DDS 詳細プロパティが `public` で露出している。
- 「Context (= プロセス・ドメイン共通の DDS 資源)」「Node (= ユーザー操作対象)」を 1 クラスが兼務しており、ROS 2 の rcl 二層モデルと一致しない。
- `ROSettaDDS.Rcl` 名前空間は `Naming/TopicNameMangler` など補助的クラスしか持たず、ROS 寄り API の置き場所として機能していない。

直近の `docs/superpowers/specs/2026-06-30-domain-participant-testability-design.md` らは *内部* の責務分離 (`ParticipantTransportSet` / `UserEndpointManager` / `ParticipantEndpointFactory` / `SedpEndpointAdvertiser` / `LeaseExpiryMonitor`) までで止まっており、公開 API には手を入れていない。

## 目標

`ROSettaDDS.Dds.DomainParticipant` の責務を rcl 風に 2 層に分け、ROS 2 ユーザー操作の主入口となる `ROSettaDDS.Rcl.Node` を新設する。

- `ROSettaDDS.Rcl.Context` がドメイン共通の DDS 資源 (transport / DiscoveryDb / SPDP / SEDP) を所有する。
- `ROSettaDDS.Rcl.Node` が `Context` を参照し、Publisher / Subscription / ServiceClient のみを生やす。
- 既存 `DomainParticipant` は `[Obsolete]` で残し、内部で `Rcl.Context` + `Rcl.Node` を生成して委譲する。破壊的変更は伴わない (コンパイル時に Obsolete 警告のみ)。
- 既存 `DomainParticipant` の **public DDS 詳細プロパティ** (`Guid` / `GuidPrefix` / `DiscoveryDb` / `UserMulticastTransport` / `UserUnicastTransport` / `UserMulticastDestination` / `ResolvedParticipantId`) は **本 PR では `Obsolete` マークを付与せず、Context 側へ委譲する形で残す**。理由: 60+ ファイルから参照されており、警告を一度に出すと既存テスト・Unity 統合テストが警告ノイズで埋まるため。**`Create*` メソッドのみ** `[Obsolete]` を付ける (ユーザー操作対象なので移行しやすい)。プロパティの `[Obsolete]` 化と削除は別 PR。
- 1 つの `Context` に対して複数の `Node` を作れるようにする (テスト容易化・プロセス内シミュレーション用途)。

## 非目標

- `Publisher` / `Subscription` / `ServiceClient` 内部実装の変更
- `ParticipantTransportSet` / `UserEndpointManager` / `ParticipantEndpointFactory` の再設計 (内部で動く実体には触らない)
- `create_service` 相当のサーバー側 API 追加 (将来検討)
- parameters / timers / clock / logger を `Node` 側に持たせる (将来検討)
- ROS 名前空間 (node namespace) 対応 (将来検討)
- 既存テスト 60+ ファイルの本格的移行 (別 PR)
- `[Obsolete]` マークのついた `DomainParticipant` の public プロパティ削除 (別 PR)

## 設計

### 全体アーキテクチャ

```
┌────────────────────────────────────────┐
│ ROSettaDDS.Dds.DomainParticipant       │  ← 公開 (Obsolete)
│   internal:                             │
│     - Rcl.Context を作る                │
│     - Rcl.Node を作る (EntityName を   │
│       node name として渡す)             │
│     - Start/Stop/Create* は Node に    │
│       委譲                              │
└────────────────────────────────────────┘

┌────────────────────────────────────────┐
│ ROSettaDDS.Rcl.Context  (公開・新設)    │
│   - UDP transport × 4 の所有             │
│   - DiscoveryDb / SPDP / SEDP           │
│   - GuidPrefix / Guid                   │
│   - Logger / Start / Stop / Dispose     │
│   - Node を 0 個以上ホスト可            │
└────────────────────────────────────────┘
              ▲
              │ (参照)
              │
┌────────────────────────────────────────┐
│ ROSettaDDS.Rcl.Node  (公開・新設)       │
│   - Publisher<T> / Subscription<T> /    │
│     ServiceClient<TReq,TRes>            │
│   - Dispose で Context には触らない     │
│     (Context 寿命 > Node 寿命を強制)    │
└────────────────────────────────────────┘
```

### ROSettaDDS.Rcl.Context

```csharp
namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_context_t 相当。ドメイン共通の DDS 資源を所有する。
/// 1 プロセス内で複数 Node をホストできる。
/// </summary>
public sealed class Context : IDisposable
{
    public Context(ContextOptions options);

    public Guid Guid { get; }
    public GuidPrefix GuidPrefix { get; }
    public int ResolvedParticipantId { get; }
    public ILogger Logger { get; }
    public ContextOptions Options { get; }

    public void Start();
    public void Stop();
    public void Dispose();
}
```

**内部実装:**

- 既存の `DomainParticipant` から Pub/Sub 関連 (`CreatePublisher` / `CreateSubscription` / `CreateServiceClient`) と `UserEntityIdAllocator` 周りを除いた部分を抽出する。
- 実装の主体は既存 `DomainParticipant` コンストラクタのロジック (transport 4 種生成 / DiscoveryDb / SPDP / SEDP / LeaseExpiryMonitor / ParticipantRtpsReceiver / `ParticipantEndpointFactory` のうち Pub/Sub 非依存部分)。
- `Start()` は transport 開始 + SPDP writer / SEDP writers / LeaseExpiryMonitor 開始まで。user endpoint writer は `Node.CreatePublisher` 内で `Publisher.Start()` により個別に Start されるため、`Context.Start` 側で取りまとめて呼ぶ必要はない。
- `Stop()` は lease → SPDP / SEDP → transport の順。
- `Dispose()` は `Stop()` 後に transport 解放・各 reader/writer 解放。
- `UserEndpointManager` / `ParticipantEndpointFactory` 相当の user endpoint 集合は **Node 側** に持つ (詳細は [Node ↔ Context 境界](#node--context-境界) で後述)。Context は Node のリストだけを保持する。

### ROSettaDDS.Rcl.ContextOptions

```csharp
namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Context"/> の構成オプション。
/// 現在の <see cref="ROSettaDDS.Dds.DomainParticipantOptions"/> の DDS 資源に関する
/// プロパティ (DomainId / ParticipantId / Transport 関連 / DiscoveryLimits など) を内包する。
/// </summary>
public sealed class ContextOptions
{
    public int DomainId { get; init; }
    public int ParticipantId { get; init; } = 0;
    public bool AutoProbeParticipantId { get; init; } = true;
    public TimeSpan SpdpInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan SedpInterval { get; init; } = TimeSpan.FromSeconds(3);
    public Duration LeaseDuration { get; init; } = Duration.FromSeconds(20);
    public TimeSpan UserWriterHeartbeatPeriod { get; init; } = TimeSpan.FromSeconds(1);
    public int UserWriterHistoryDepth { get; init; } = 1000;
    public IPAddress? MulticastInterface { get; init; }
    public IPAddress MulticastGroup { get; init; } = RtpsConstants.DefaultMulticastAddress;
    public IPAddress? LocalUnicastAddress { get; init; }
    public bool LocalhostOnly { get; init; }
    public string EntityName { get; init; } = "rosettadds_context";
    public VendorId VendorId { get; init; } = VendorId.ROSettaDDS;
    public ProtocolVersion ProtocolVersion { get; init; } = ProtocolVersion.V2_4;
    public ILogger Logger { get; init; } = NullLogger.Instance;
    public DataFragReassemblyOptions DataFragReassembly { get; init; } = DataFragReassemblyOptions.Default;
    public CdrReadLimits CdrReadLimits { get; init; } = CdrReadLimits.Default;
    public DiscoveryLimits DiscoveryLimits { get; init; } = DiscoveryLimits.Default;
    public IRtpsTransport? CustomMulticastTransport { get; init; }
    public IRtpsTransport? CustomUnicastTransport { get; init; }
    public IRtpsTransport? CustomUserMulticastTransport { get; init; }
    public IRtpsTransport? CustomUserUnicastTransport { get; init; }

    /// <summary>既存 <see cref="ROSettaDDS.Dds.DomainParticipantOptions"/> からの内部変換用ファクトリ。</summary>
    internal static ContextOptions FromLegacy(DomainParticipantOptions legacy);
}
```

`DomainParticipantOptions` との重複を最小化するため、Public surface としては `ContextOptions` を正とし、`DomainParticipantOptions` 内部で `ContextOptions` へ変換する。`FromLegacy` は `internal` とし、互換期間中の橋渡しにのみ使う。

### ROSettaDDS.Rcl.Node

```csharp
namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_node_t (rclcpp::Node) 相当。<see cref="Context"/> を参照し、
/// Publisher / Subscription / ServiceClient のみを生やす薄いラッパ。
/// </summary>
public sealed class Node : IDisposable
{
    public Node(Context context, string name, NodeOptions? options = null);

    public string Name { get; }
    public Context Context { get; }
    public NodeOptions Options { get; }  // コンストラクタで options ?? NodeOptions.Default を保持

    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null);
    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null);

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null);

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null);

    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName);

    public void Dispose();
}
```

**内部実装:**

- `CreatePublisher<T>` / `CreateSubscription<T>` / `CreateServiceClient<TReq,TRes>` の実装は、現状 `DomainParticipant` が呼んでいる `ParticipantEndpointFactory.CreateWriter` / `CreateReader` / `CreateReliableReplyReader` をそのまま Context 側に委譲する形になる。
- Topic/Type のマングリングは `ROSettaDDS.Rcl.Naming.TopicNameMangler` / `TypeNameMangler` をそのまま使う。
- QoS 規定値は現状の `DomainParticipant` と同じく `ReliabilityQos.Reliable` / `DurabilityQos.Volatile` (subscription の reliability は nullable 既定で Reliable)。

### ROSettaDDS.Rcl.NodeOptions

```csharp
namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Node"/> の構成オプション。
/// 初回リリースでは logger override のみ。将来的に namespace / parameter_override などを追加する。
/// </summary>
public sealed class NodeOptions
{
    public ILogger? Logger { get; init; }

    public static NodeOptions Default { get; } = new();
}
```

### Node ↔ Context 境界

複数 Node が同一 Context を共有できるようにするため、`UserEndpointManager` / `ParticipantEndpointFactory` 相当の user endpoint 集合は **各 Node が個別に所有する**。`Context` はそれらを **所有しない** が、**寿命管理 (Dispose 時の後始末)** は `Context.Dispose` 経由で行う。

```csharp
// Node 内部 (現状の DomainParticipant から Pub/Sub 部分を抽出)
private readonly UserEndpointManager _userEndpoints;
private readonly ParticipantEndpointFactory _endpointFactory;

// Context 内部
private readonly List<Node> _nodes = new();
internal void RegisterNode(Node node);
internal void UnregisterNode(Node node);
internal void DisposeAllNodes();  // Context.Dispose 時に呼ばれる
```

これは以下を根拠とした現実的なトレードオフ判断:

- 「複数 Node を同一 Context にぶら下げて使う」ユースケースはテスト容易化目的が主。実 ROS 2 ノード相当のユーザー操作対象を 1 プロセス内に複数持ちたいケースは稀。
- 1 Node 内に複数 publisher / subscription を持つユースケース (現状の `DomainParticipant` の使い方) は 1 Node が完結するため、本 PR のスコープで完全にカバーされる。
- 完全な user endpoint 共有 (topic 名での lookup による endpoint 共有) は別 PR とする。1 つの `UserEndpointManager` を Context 側に持ち上げると、Node 削除時に該当 Node の endpoint だけを取り除くロジックを `UserEndpointManager` に追加する必要があり、既存テスト (`UserEndpointManagerTests.cs` 2 件) にも影響する。本 PR はそれを避ける。

**Node の寿命 > Context の寿命は許さない**:

- `Node` コンストラクタは `Context` の参照を保持する。
- `Context.Dispose()` 呼び出し時、生き残っている全 `Node` を先に `Dispose` してから DDS 資源を解放する (`UnregisterAllLocalEndpoints` → 各 reader/writer dispose → SPDP / SEDP / transports)。
- `Node.Dispose()` 後に `Context.Dispose()` を呼んでも、Node 側の user endpoint 集合が先に解放済みなので安全。
- `Node.Dispose()` 後に当該 Node の `Create*` を呼ぶと `ObjectDisposedException` を投げる。

### 既存 DomainParticipant の扱い

```csharp
namespace ROSettaDDS.Dds;

[Obsolete("Use ROSettaDDS.Rcl.Context + ROSettaDDS.Rcl.Node instead. " +
          "DomainParticipant will be removed in a future release.")]
public sealed class DomainParticipant : IDisposable
{
    private readonly Rcl.Context _context;
    private readonly Rcl.Node _node;

    public DomainParticipant(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;
        var ctxOpts = Rcl.ContextOptions.FromLegacy(options);
        _context = new Rcl.Context(ctxOpts);
        _node = new Rcl.Node(_context, options.EntityName);
    }

    public Guid Guid => _context.Guid;
    public GuidPrefix GuidPrefix => _context.GuidPrefix;
    public DiscoveryDb DiscoveryDb => _context.DiscoveryDb;  // Context 側に internal/public 切り替え
    public DomainParticipantOptions Options => _options;
    public int ResolvedParticipantId => _context.ResolvedParticipantId;
    public IRtpsTransport UserMulticastTransport => _context.UserMulticastTransport;
    public IRtpsTransport UserUnicastTransport => _context.UserUnicastTransport;
    public Locator UserMulticastDestination => _context.UserMulticastDestination;

    public void Start() => _context.Start();
    public void Stop() => _context.Stop();
    public void Dispose() { _node.Dispose(); _context.Dispose(); }

    [Obsolete("Use Node.CreatePublisher instead.")]
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => _node.CreatePublisher<T>(topicName, serializer, typeName);

    [Obsolete("Use Node.CreatePublisher instead.")]
    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
        => _node.CreatePublisher<T>(topicName, serializer, reliability, durability, typeName);

    [Obsolete("Use Node.CreateSubscription instead.")]
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => _node.CreateSubscription<T>(topicName, serializer, handler, typeName, handlerContext, reliability);

    [Obsolete("Use Node.CreateSubscription instead.")]
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => _node.CreateSubscription<T>(topicName, serializer, handler, handlerContext, reliability);

    [Obsolete("Use Node.CreateServiceClient instead.")]
    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
        => _node.CreateServiceClient<TRequest, TResponse>(descriptor, serviceName);
}
```

**ポイント:**

- `DomainParticipant` の `public` プロパティ (`Guid` / `GuidPrefix` / `DiscoveryDb` / `UserMulticastTransport` / `UserUnicastTransport` / `UserMulticastDestination` / `ResolvedParticipantId`) は **本 PR では `Obsolete` マークを付与せず**、Context 側へ委譲する形で残す (前述「目標」セクション参照)。削除は別 PR。
- `Options` プロパティは `DomainParticipantOptions` を返す既存 API を保持する必要があるため、`_options` フィールドを内部に持つ。
- `BuildParticipantData` / `OnRemoteParticipantDiscovered` / `OnRemoteParticipantLost` など、`internal` メンバは `Context` 側に移すか、`Context` 側の internal メソッドを `DomainParticipant` から呼ぶ形に変更する。
- `_userEndpoints` / `ParticipantEndpointFactory` は **Node 側** の private/internal に移る。`_spdpReader` / `_spdpWriter` / `_sedp*` / `_leaseExpiryMonitor` / `_receiver` / `_transports` は **Context 側** の private/internal に移る。

### Context 内部と ParticipantTransportSet 等の関係

`Context` コンストラクタは概ね現在の `DomainParticipant` コンストラクタの `GuidPrefix` 構築以降を再現するが、`ParticipantTransportSet` / `UserEndpointManager` / `ParticipantEndpointFactory` / `SedpEndpointAdvertiser` / `LeaseExpiryMonitor` といった既存コンポーネントは内部実装としてそのまま使う。

- `ParticipantTransportSet` のコンストラクタシグネチャ (現状 `Create(DomainParticipantOptions)`) を `Create(ContextOptions)` へ変更する (内部リファクタ)。
- `UserEndpointManager` / `ParticipantEndpointFactory` の参照は **Node 側** に移る (前述 [Node ↔ Context 境界](#node--context-境界) 参照)。`Context` は transport / SPDP / SEDP / DiscoveryDb / LeaseExpiryMonitor / ParticipantRtpsReceiver だけを所有する。
- `Node` 側の `Create*` メソッドは内部で `ParticipantEndpointFactory` (Node 私有) と `UserEndpointManager` (Node 私有) を呼ぶ。`Context` の `IRtpsTransport` などの DDS 資源を借用する形になる。

## ファイル配置

**新規ファイル (`src/rosettadds/Rcl/` 配下):**

- `Context.cs` (+ `.meta`)
- `ContextOptions.cs` (+ `.meta`)
- `Node.cs` (+ `.meta`)
- `NodeOptions.cs` (+ `.meta`)

**変更ファイル:**

- `src/rosettadds/Dds/DomainParticipant.cs` — 内部を `Rcl.Context` + `Rcl.Node` への委譲に置き換え。`[Obsolete]` マーク追加。
- `src/rosettadds/Dds/DomainParticipantOptions.cs` — そのまま残す (既存 API)。`ContextOptions.FromLegacy` の呼び出し元として内部的に使われる。
- `src/rosettadds/Dds/ParticipantTransportSet.cs` — `Create(DomainParticipantOptions)` を `Create(ContextOptions)` へ変更 (内部シグネチャ変更、public surface への影響は限定的)。
- `src/rosettadds/rosettadds.csproj` — 必要に応じて `<Compile Include>` の glob 確認 (.csproj が glob include なら変更不要)。

**`src/rosettadds/Rcl/Naming/`** は据え置き。

## 既存 API の migration 範囲

- 60+ ファイルから `new DomainParticipant(...)` / `participant.CreatePublisher` 等が参照されている。**本 PR ではこれらを変更しない**。
- `[Obsolete]` 警告は出るが、テストは緑のまま。
- 既存 Unity 統合テスト・Perf Harness・samples (TalkerListener / SpdpDemo) の Rcl.Context / Rcl.Node への移行は別 PR で行う。
- README.ja.md / README.md の更新も別 PR。

## テスト方針 (TDD)

`docs/superpowers/specs/2026-06-30-domain-participant-testability-design.md` と同じ TDD フローを踏襲する。

**新規テストファイル (`tests/rosettadds.Tests/Rcl/` 配下):**

| ファイル | テスト対象 | 主要ケース |
|---|---|---|
| `ContextTests.cs` | `Rcl.Context` | コンストラクタの transport 4 種生成、GuidPrefix 生成、Start/Stop の冪等性、Dispose 後の Start/Stop 拒否、`ContextOptions` の既定値 |
| `ContextOptionsTests.cs` | `Rcl.ContextOptions` | 既定値、`FromLegacy` の各プロパティ転送、`LocalhostOnly` / `LocalUnicastAddress` の優先順位 |
| `NodeTests.cs` | `Rcl.Node` | Context 参照、Name の保持、CreatePublisher / CreateSubscription / CreateServiceClient の正常系、Dispose 後の Create* 拒否、QoS 規定値 |
| `NodeOptionsTests.cs` | `Rcl.NodeOptions` | `Default` の singleton 性、Logger override |
| `DomainParticipantObsoleteTests.cs` | 既存 `DomainParticipant` 互換 | 既存挙動が保たれることの smoke test。`[Obsolete]` 抑制属性を付けてコンパイル警告を抑止 |

**受け入れ基準:**

- 新規 unit test が緑
- 既存 `tests/rosettadds.Tests/Integration/*` (PubSubLoopback / SedpLoopback / ServiceClientLoopback / PublisherHotPath 等) が緑のまま
- 既存 `UserEndpointManagerTests.cs` / `ParticipantTransportSetTests.cs` / `LeaseExpiryMonitorTests.cs` が緑のまま
- `samples/TalkerListener` / `samples/SpdpDemo` が変更なしでビルド・実行可能
- Unity に取り込まれる新規ファイルに `.meta` を追加し、`.github/scripts/check_unity_meta.sh` がクリーン

**Unity .meta ファイル:**

- `src/rosettadds/Rcl/Context.cs` / `ContextOptions.cs` / `Node.cs` / `NodeOptions.cs` の各 `.meta` を生成・コミット
- `Rcl` ディレクトリ自体の `.meta` は既存 (`Naming.meta` の兄弟として既に存在)。追加不要

## 影響範囲まとめ

**新規ファイル:**

- `src/rosettadds/Rcl/Context.cs` (+ `.meta`)
- `src/rosettadds/Rcl/ContextOptions.cs` (+ `.meta`)
- `src/rosettadds/Rcl/Node.cs` (+ `.meta`)
- `src/rosettadds/Rcl/NodeOptions.cs` (+ `.meta`)
- `tests/rosettadds.Tests/Rcl/ContextTests.cs`
- `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`
- `tests/rosettadds.Tests/Rcl/NodeTests.cs`
- `tests/rosettadds.Tests/Rcl/NodeOptionsTests.cs`
- `tests/rosettadds.Tests/Rcl/DomainParticipantObsoleteTests.cs`

注: `src/rosettadds/Rcl.meta` は既存 (`Naming.meta` の兄弟)。`tests/rosettadds.Tests/Rcl/` ディレクトリも新規作成する。

**変更ファイル:**

- `src/rosettadds/Dds/DomainParticipant.cs` — 委譲パターンへの書き換え + `[Obsolete]`
- `src/rosettadds/Dds/ParticipantTransportSet.cs` — `Create` の引数を `ContextOptions` へ
- (必要なら) `src/rosettadds/rosettadds.csproj` — glob 確認のみ

**変更なし:**

- `src/rosettadds/Dds/Publisher.cs` / `Subscription.cs` / `ServiceClient.cs`
- `src/rosettadds/Dds/UserEndpointManager.cs` / `ParticipantEndpointFactory.cs` / `SedpEndpointAdvertiser.cs` / `LeaseExpiryMonitor.cs`
- `src/rosettadds/Dds/DiscoveryDb.cs` / `SpdpBuiltin*` / `SedpEndpoint*`
- `src/rosettadds/Rtps/*` / `Transport/*` / `Discovery/*`
- `src/rosettadds/Rcl/Naming/*`
- 全テストファイル (新規追加は除く)
- 全 samples / Unity 統合テスト / Perf Harness
- README

## 段階リリース計画

1. **本 PR (この spec)**: `Rcl.Context` / `Rcl.Node` / `Rcl.ContextOptions` / `Rcl.NodeOptions` を新設。`DomainParticipant` を委譲パターンへ書き換え、`[Obsolete]` 付与。テスト追加。既存テスト緑のまま。サンプル・README は無変更。
2. **後続 PR**: README.ja.md / README.md を Rcl.Context + Rcl.Node を使った quickstart に更新。
3. **後続 PR**: `samples/TalkerListener` / `samples/SpdpDemo` を Rcl.Context + Rcl.Node 起点に書き換え。
4. **後続 PR**: Unity 統合テスト・Perf Harness 内の `DomainParticipant` 参照を Rcl.Context + Rcl.Node に移行。
5. **将来 PR**: `DomainParticipant` の public DDS 詳細プロパティ (`GuidPrefix` / `DiscoveryDb` / transport 各種) を `[Obsolete]` 化 → 削除。
6. **将来 PR**: `DomainParticipant` 自体を削除。

## 受け入れ基準チェックリスト

- [ ] `Rcl.Context` / `Rcl.Node` / `Rcl.ContextOptions` / `Rcl.NodeOptions` の unit test が緑
- [ ] `Rcl.Node` 経由の `CreatePublisher` / `CreateSubscription` / `CreateServiceClient` が unit test で確認できる
- [ ] 既存 `tests/rosettadds.Tests/Integration/*` が緑のまま
- [ ] 既存 `samples/TalkerListener` / `samples/SpdpDemo` が変更なしでビルド・実行可能
- [ ] 既存 `DomainParticipant` の挙動 (Pub/Sub 送受信、service client 送受信) が smoke test で確認できる
- [ ] `check_unity_meta.sh` がクリーン
- [ ] `ROSettaDDS.Rcl` 名前空間のドキュメントコメントが日本語で整備されている
- [ ] DomainParticipant の public メンバに `[Obsolete]` 属性と誘導メッセージが付与されている
- [ ] `DomainParticipant` の `Create*` メソッドから `Rcl.Node` への委譲時に例外が発生しない
