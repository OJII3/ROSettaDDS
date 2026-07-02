# Rcl.Context / Rcl.Node 分離実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ROSettaDDS.Dds.DomainParticipant` を rcl 風の 2 層 (`ROSettaDDS.Rcl.Context` + `ROSettaDDS.Rcl.Node`) に分離し、ROS 2 の `rcl_context_t` / `rcl_node_t` に対応する公開 API を提供する。既存 `DomainParticipant` は `[Obsolete]` 委譲ラッパとして残し、既存テスト 60+ ファイルは破壊しない。

**Architecture:** `Context` がドメイン共通 DDS 資源 (transport 4 種 / DiscoveryDb / SPDP / SEDP / LeaseExpiryMonitor / ParticipantRtpsReceiver) を所有する。`Node` は `Context` を参照し、Pub/Sub/Client の user endpoint 集合 (`UserEndpointManager` + `ParticipantEndpointFactory`) を個別に所有する。`DomainParticipant` は `Context` + `Node` を内部生成するだけの薄いラッパになる。

**Tech Stack:** C# / .NET 8, xUnit, FluentAssertions. Unity package under `src/rosettadds` with `.meta` files. `tests/rosettadds.Tests/` でループバック integration 確認。

**前提:**
- ブランチ: `rcl/context-node-design`
- 設計 spec: `docs/superpowers/specs/2026-07-02-rcl-context-node-design.md` (441 行)
- 既存 `DomainParticipant.cs` (518 行) は責務分離対象だが、public API は **完全保持** (`[Obsolete]` 付与のみ)

---

## File Structure

### 新規ファイル

- `src/rosettadds/Rcl/Context.cs` (+ `.meta`)
  - DDS 資源を所有する `public sealed class Context : IDisposable`。
- `src/rosettadds/Rcl/ContextOptions.cs` (+ `.meta`)
  - `Context` の構成オプション。`DomainParticipantOptions` と同じプロパティを持つ + `internal static FromLegacy`。
- `src/rosettadds/Rcl/Node.cs` (+ `.meta`)
  - Pub/Sub/Client を生やす `public sealed class Node : IDisposable`。`Context` を参照。
- `src/rosettadds/Rcl/NodeOptions.cs` (+ `.meta`)
  - 初回リリースは `Logger?` のみ。`Default` 静的 singleton。
- `tests/rosettadds.Tests/Rcl/ContextTests.cs`
- `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`
- `tests/rosettadds.Tests/Rcl/NodeTests.cs`
- `tests/rosettadds.Tests/Rcl/NodeOptionsTests.cs`
- `tests/rosettadds.Tests/Rcl/DomainParticipantObsoleteTests.cs`

### 変更ファイル

- `src/rosettadds/Dds/DomainParticipant.cs` — 内部実装を削除し、`Rcl.Context` + `Rcl.Node` への委譲に置き換え。`[Obsolete]` マーク追加 (`Create*` のみ)。
- `src/rosettadds/Dds/ParticipantTransportSet.cs` — `Create` および内部 `ProbeUnicastTransports` の引数を `ContextOptions` に変更 (内部シグネチャ変更のみ)。
- `src/rosettadds/Dds/ParticipantEndpointFactory.cs` — コンストラクタ引数を `ContextOptions` に変更。
- `src/rosettadds/Dds/LeaseExpiryMonitor.cs` — コンストラクタと `ComputeCheckPeriod` の引数を `ContextOptions` に変更。
- `src/rosettadds/Dds/DomainParticipantOptions.cs` — 据え置き (public API 維持)。`ContextOptions.FromLegacy` の入力として使われる。
- `tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs` — 既存テストを `new ContextOptions` 起点に書き換え (5 件)。
- `tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs` — 既存テストを `new ContextOptions` 起点に書き換え (2 件)。

### 変更なし

- `src/rosettadds/Dds/Publisher.cs` / `Subscription.cs` / `ServiceClient.cs` / `UserEndpointManager.cs` / `SedpEndpointAdvertiser.cs`
- `src/rosettadds/Discovery/*` / `src/rosettadds/Rtps/*` / `src/rosettadds/Transport/*`
- `src/rosettadds/Rcl/Naming/*`
- `src/rosettadds/Common/*` / `src/rosettadds/Cdr/*`
- `tests/rosettadds.Tests/Integration/*` (DomainParticipant をそのまま使うので新 API と無関係)
- `samples/TalkerListener` / `samples/SpdpDemo`
- `Ros2Unity/Assets/Tests/*` / `Ros2Unity/Assets/Perf/*`
- `README.md` / `README.ja.md`

---

## Phase 0: 事前確認

### Task 0.1: 現在の状態確認

- [ ] **Step 1: ブランチ確認**

```bash
git branch --show-current
```

期待: `rcl/context-node-design`

- [ ] **Step 2: 既存テストが緑であることを確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj
```

期待: 全件 PASS。失敗するテストがあれば本プラン開始前に修正。

- [ ] **Step 3: コミット**

変更なしならコミット不要 (clean tree のまま進む)。

---

## Phase 1: ContextOptions + NodeOptions

### Task 1.1: ContextOptions のスケルトンとデフォルト値

**Files:**
- Create: `src/rosettadds/Rcl/ContextOptions.cs` (+ `.meta`)
- Test: `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`

- [ ] **Step 1: テスト作成**

`tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`:

```csharp
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class ContextOptionsTests
{
    [Fact]
    public void 既定値は_DomainParticipantOptions_と一致する()
    {
        var opts = new ContextOptions();

        Assert.Equal(0, opts.DomainId);
        Assert.Equal(0, opts.ParticipantId);
        Assert.True(opts.AutoProbeParticipantId);
        Assert.Equal(TimeSpan.FromSeconds(3), opts.SpdpInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), opts.SedpInterval);
        Assert.Equal(Duration.FromSeconds(20), opts.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), opts.UserWriterHeartbeatPeriod);
        Assert.Equal(1000, opts.UserWriterHistoryDepth);
        Assert.Null(opts.MulticastInterface);
        Assert.Equal(RtpsConstants.DefaultMulticastAddress, opts.MulticastGroup);
        Assert.Null(opts.LocalUnicastAddress);
        Assert.False(opts.LocalhostOnly);
        Assert.Equal("rosettadds_context", opts.EntityName);
        Assert.Equal(VendorId.ROSettaDDS, opts.VendorId);
        Assert.Equal(ProtocolVersion.V2_4, opts.ProtocolVersion);
        Assert.Same(NullLogger.Instance, opts.Logger);
        Assert.Null(opts.CustomMulticastTransport);
        Assert.Null(opts.CustomUnicastTransport);
        Assert.Null(opts.CustomUserMulticastTransport);
        Assert.Null(opts.CustomUserUnicastTransport);
    }
}
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextOptionsTests"
```

期待: FAIL (ContextOptions 未定義 → CS0246)。

- [ ] **Step 3: ContextOptions スケルトン実装**

`src/rosettadds/Rcl/ContextOptions.cs`:

```csharp
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Context"/> の構成オプション。
/// <see cref="ROSettaDDS.Dds.DomainParticipantOptions"/> の DDS 資源に関するプロパティを
/// そのまま受け継ぐ。public な正本はこちら。
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
}
```

注: `DataFragReassemblyOptions` / `CdrReadLimits` / `DiscoveryLimits` の using が必要:

```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Discovery;
```

ファイル先頭に追加すること。

- [ ] **Step 4: Unity .meta 生成**

`.github/scripts/check_unity_meta.sh` の規約に従い、`src/rosettadds/Rcl/ContextOptions.cs.meta` を生成。既存 `src/rosettadds/Rcl/Naming/TopicNameMangler.cs.meta` を雛形にして、guid と fileID を書き換える。

```bash
cat src/rosettadds/Rcl/Naming/TopicNameMangler.cs.meta
```

を参考にする。`guid` と `TextureImporter` などの固有値は Unity Editor が再生成するため、.meta 自体の構造 (fileFormatVersion, MonoImporter セクション) を踏襲する。

- [ ] **Step 5: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextOptionsTests"
```

期待: 1 件 PASS。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Rcl/ContextOptions.cs src/rosettadds/Rcl/ContextOptions.cs.meta tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs
git commit -m "feat(rcl): ContextOptions を新設 (既定値テスト付き)"
```

### Task 1.2: FromLegacy 変換

**Files:**
- Modify: `src/rosettadds/Rcl/ContextOptions.cs`
- Modify: `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`

- [ ] **Step 1: テスト追加**

`tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs` の末尾に追記:

```csharp
    [Fact]
    public void FromLegacy_は全プロパティを転送する()
    {
        var legacy = new ROSettaDDS.Dds.DomainParticipantOptions
        {
            DomainId = 42,
            ParticipantId = 7,
            AutoProbeParticipantId = false,
            SpdpInterval = TimeSpan.FromSeconds(11),
            SedpInterval = TimeSpan.FromSeconds(13),
            LeaseDuration = Duration.FromSeconds(31),
            UserWriterHeartbeatPeriod = TimeSpan.FromSeconds(2),
            UserWriterHistoryDepth = 500,
            LocalhostOnly = true,
            EntityName = "legacy_name",
            Logger = NullLogger.Instance,
        };

        var ctx = ContextOptions.FromLegacy(legacy);

        Assert.Equal(42, ctx.DomainId);
        Assert.Equal(7, ctx.ParticipantId);
        Assert.False(ctx.AutoProbeParticipantId);
        Assert.Equal(TimeSpan.FromSeconds(11), ctx.SpdpInterval);
        Assert.Equal(TimeSpan.FromSeconds(13), ctx.SedpInterval);
        Assert.Equal(Duration.FromSeconds(31), ctx.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(2), ctx.UserWriterHeartbeatPeriod);
        Assert.Equal(500, ctx.UserWriterHistoryDepth);
        Assert.True(ctx.LocalhostOnly);
        Assert.Equal("legacy_name", ctx.EntityName);
        Assert.Same(NullLogger.Instance, ctx.Logger);
    }
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~FromLegacy"
```

期待: FAIL (CS0103 FromLegacy 未定義)。

- [ ] **Step 3: FromLegacy 実装**

`src/rosettadds/Rcl/ContextOptions.cs` の `}` 直前に追加:

```csharp
    /// <summary>既存 <see cref="ROSettaDDS.Dds.DomainParticipantOptions"/> からの内部変換。</summary>
    internal static ContextOptions FromLegacy(ROSettaDDS.Dds.DomainParticipantOptions legacy)
    {
        if (legacy is null) throw new ArgumentNullException(nameof(legacy));
        return new ContextOptions
        {
            DomainId = legacy.DomainId,
            ParticipantId = legacy.ParticipantId,
            AutoProbeParticipantId = legacy.AutoProbeParticipantId,
            SpdpInterval = legacy.SpdpInterval,
            SedpInterval = legacy.SedpInterval,
            LeaseDuration = legacy.LeaseDuration,
            UserWriterHeartbeatPeriod = legacy.UserWriterHeartbeatPeriod,
            UserWriterHistoryDepth = legacy.UserWriterHistoryDepth,
            MulticastInterface = legacy.MulticastInterface,
            MulticastGroup = legacy.MulticastGroup,
            LocalUnicastAddress = legacy.LocalUnicastAddress,
            LocalhostOnly = legacy.LocalhostOnly,
            EntityName = legacy.EntityName,
            VendorId = legacy.VendorId,
            ProtocolVersion = legacy.ProtocolVersion,
            Logger = legacy.Logger,
            DataFragReassembly = legacy.DataFragReassembly,
            CdrReadLimits = legacy.CdrReadLimits,
            DiscoveryLimits = legacy.DiscoveryLimits,
            CustomMulticastTransport = legacy.CustomMulticastTransport,
            CustomUnicastTransport = legacy.CustomUnicastTransport,
            CustomUserMulticastTransport = legacy.CustomUserMulticastTransport,
            CustomUserUnicastTransport = legacy.CustomUserUnicastTransport,
        };
    }
```

- [ ] **Step 4: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextOptionsTests"
```

期待: 2 件 PASS。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rcl/ContextOptions.cs tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs
git commit -m "feat(rcl): ContextOptions.FromLegacy で既存 DomainParticipantOptions から変換"
```

### Task 1.3: NodeOptions

**Files:**
- Create: `src/rosettadds/Rcl/NodeOptions.cs` (+ `.meta`)
- Test: `tests/rosettadds.Tests/Rcl/NodeOptionsTests.cs`

- [ ] **Step 1: テスト作成**

`tests/rosettadds.Tests/Rcl/NodeOptionsTests.cs`:

```csharp
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class NodeOptionsTests
{
    [Fact]
    public void 既定の_Logger_は_null()
    {
        var opts = new NodeOptions();
        Assert.Null(opts.Logger);
    }

    [Fact]
    public void Default_は新しいインスタンス()
    {
        // Default は値型ではなく object なので毎回作っても singleton である必要は薄い。
        // ただしアクセス可能であることを確認する。
        var a = NodeOptions.Default;
        var b = NodeOptions.Default;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(a.Logger);
        Assert.Null(b.Logger);
    }

    [Fact]
    public void Logger_を_override_できる()
    {
        var custom = new ConsoleLogger("test", LogLevel.Debug);
        var opts = new NodeOptions { Logger = custom };
        Assert.Same(custom, opts.Logger);
    }
}
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeOptionsTests"
```

期待: FAIL (NodeOptions 未定義)。

- [ ] **Step 3: NodeOptions 実装**

`src/rosettadds/Rcl/NodeOptions.cs`:

```csharp
using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Node"/> の構成オプション。初回リリースでは Logger override のみ。
/// 将来的に namespace / parameter_override などを追加する。
/// </summary>
public sealed class NodeOptions
{
    /// <summary>Node 専用ロガー。null のとき Context の Logger を使う。</summary>
    public ILogger? Logger { get; init; }

    public static NodeOptions Default { get; } = new();
}
```

- [ ] **Step 4: Unity .meta 生成**

`src/rosettadds/Rcl/NodeOptions.cs.meta` を `ContextOptions.cs.meta` と同じ構造で作成。

- [ ] **Step 5: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeOptionsTests"
```

期待: 3 件 PASS。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Rcl/NodeOptions.cs src/rosettadds/Rcl/NodeOptions.cs.meta tests/rosettadds.Tests/Rcl/NodeOptionsTests.cs
git commit -m "feat(rcl): NodeOptions を新設 (Logger override のみ)"
```

---

## Phase 2: 内部コンポーネントを ContextOptions 化

> **目的:** `ParticipantTransportSet` / `ParticipantEndpointFactory` / `LeaseExpiryMonitor` の入力型を `DomainParticipantOptions` から `ContextOptions` に切り替える。**`DomainParticipant` を含む既存呼び出し元を順次更新する** ので、各タスクの終端でビルドが緑であることを確認する。

### Task 2.1: ParticipantTransportSet.Create の引数を ContextOptions に

**Files:**
- Modify: `src/rosettadds/Dds/ParticipantTransportSet.cs` (line 59, line 167)
- Modify: `src/rosettadds/Dds/DomainParticipant.cs` (呼び出し元)

- [ ] **Step 1: 呼び出し元の確認**

`DomainParticipant.cs` の `ParticipantTransportSet.Create(_options)` 呼び出し箇所:

```
src/rosettadds/Dds/DomainParticipant.cs:65
```

```csharp
_transports = ParticipantTransportSet.Create(_options);
```

- [ ] **Step 2: ParticipantTransportSet.Create のシグネチャ変更**

`src/rosettadds/Dds/ParticipantTransportSet.cs:59` を:

```csharp
    public static ParticipantTransportSet Create(Rcl.ContextOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
```

に変更。

`src/rosettadds/Dds/ParticipantTransportSet.cs:167` の `ProbeUnicastTransports` シグネチャも:

```csharp
    private static (OwnedTransport metatraffic, OwnedTransport user, int participantId)
        ProbeUnicastTransports(
            List<OwnedTransport> created,
            Rcl.ContextOptions options,
            IPAddress localAddress)
```

に変更。

ファイル先頭に `using ROSettaDDS.Rcl;` が必要なら追加 (既にある場合は不要)。

- [ ] **Step 3: DomainParticipant 呼び出しを ContextOptions 経由に**

`src/rosettadds/Dds/DomainParticipant.cs:65` を:

```csharp
        _transports = ParticipantTransportSet.Create(Rcl.ContextOptions.FromLegacy(_options));
```

に変更。

注: `Rcl.ContextOptions.FromLegacy` は `internal` なので、同一アセンブリ内 (両方 `ROSettaDDS` アセンブリ) からのみ呼び出せる。`Dds.DomainParticipant` と `Rcl.ContextOptions` は同じ `rosettadds` プロジェクトなので問題なし。

- [ ] **Step 4: ビルド確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
```

期待: 0 エラー。ParticipantTransportSet 配下のエラーは全部修正する。

- [ ] **Step 5: テスト確認 (既存)**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ParticipantTransportSetTests"
```

期待: 既存 6 件全件 PASS (DomainParticipantOptions を使っていたテストはまだ緑のはず。これは **既存テストも ParticipantTransportSet.Create を直接呼んでいる場合は別タスクで更新する**)。

注: `ParticipantTransportSetTests.cs` は直接 `new DomainParticipantOptions` を作っているので **まだ緑のまま**。これは Task 2.5 で更新する。

- [ ] **Step 6: Integration テスト確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Integration"
```

期待: 既存 integration テストが緑のまま (DomainParticipant 経由のテストは今回のシグネチャ変更の影響を受けない)。

- [ ] **Step 7: コミット**

```bash
git add src/rosettadds/Dds/ParticipantTransportSet.cs src/rosettadds/Dds/DomainParticipant.cs
git commit -m "refactor(dds): ParticipantTransportSet.Create を ContextOptions 起点に変更"
```

### Task 2.2: ParticipantEndpointFactory を ContextOptions に

**Files:**
- Modify: `src/rosettadds/Dds/ParticipantEndpointFactory.cs` (line 17, 25)
- Modify: `src/rosettadds/Dds/DomainParticipant.cs` (呼び出し元)

- [ ] **Step 1: 呼び出し元の確認**

`DomainParticipant.cs:71-76`:

```csharp
_endpointFactory = new ParticipantEndpointFactory(
    _options,
    _transports,
    GuidPrefix,
    Guid,
    _userEntityIds);
```

- [ ] **Step 2: ParticipantEndpointFactory のシグネチャ変更**

`src/rosettadds/Dds/ParticipantEndpointFactory.cs:17` を:

```csharp
    private readonly Rcl.ContextOptions _options;
```

に。`:25` を:

```csharp
        Rcl.ContextOptions options,
```

に変更。

ファイル先頭に `using ROSettaDDS.Rcl;` を追加。

- [ ] **Step 3: DomainParticipant 呼び出しを ContextOptions 経由に**

`DomainParticipant.cs:71-76` を:

```csharp
_endpointFactory = new ParticipantEndpointFactory(
    Rcl.ContextOptions.FromLegacy(_options),
    _transports,
    GuidPrefix,
    Guid,
    _userEntityIds);
```

に変更。

- [ ] **Step 4: ビルド + Integration 確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Integration"
```

期待: 緑。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Dds/ParticipantEndpointFactory.cs src/rosettadds/Dds/DomainParticipant.cs
git commit -m "refactor(dds): ParticipantEndpointFactory を ContextOptions 起点に変更"
```

### Task 2.3: LeaseExpiryMonitor を ContextOptions に

**Files:**
- Modify: `src/rosettadds/Dds/LeaseExpiryMonitor.cs` (line 18, 28)
- Modify: `src/rosettadds/Dds/DomainParticipant.cs` (呼び出し元)

- [ ] **Step 1: 呼び出し元の確認**

`DomainParticipant.cs:69`:

```csharp
_leaseExpiryMonitor = new LeaseExpiryMonitor(_discoveryDb, _options, _options.Logger);
```

- [ ] **Step 2: LeaseExpiryMonitor のシグネチャ変更**

`src/rosettadds/Dds/LeaseExpiryMonitor.cs:18` を:

```csharp
    public LeaseExpiryMonitor(DiscoveryDb discoveryDb, Rcl.ContextOptions options, ILogger logger)
```

に。`:28` を:

```csharp
    public static TimeSpan ComputeCheckPeriod(Rcl.ContextOptions options)
```

に変更。`using ROSettaDDS.Rcl;` 追加。

- [ ] **Step 3: DomainParticipant 呼び出しを ContextOptions 経由に**

`DomainParticipant.cs:69` を:

```csharp
_leaseExpiryMonitor = new LeaseExpiryMonitor(_discoveryDb, Rcl.ContextOptions.FromLegacy(_options), _options.Logger);
```

に変更。

- [ ] **Step 4: ビルド + LeaseExpiryMonitor テスト**

```bash
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~LeaseExpiryMonitorTests"
```

期待: 緑。`LeaseExpiryMonitorTests.cs` 内で `new DomainParticipantOptions` を使っている箇所は Task 2.5 で更新する。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Dds/LeaseExpiryMonitor.cs src/rosettadds/Dds/DomainParticipant.cs
git commit -m "refactor(dds): LeaseExpiryMonitor を ContextOptions 起点に変更"
```

### Task 2.4: ParticipantTransportSetTests を ContextOptions 起点に

**Files:**
- Modify: `tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs`

- [ ] **Step 1: 既存テストの確認**

`ParticipantTransportSetTests.cs` には 6 件のテストがあり、全て `new DomainParticipantOptions { ... }` を直接作っている。

```bash
grep -n "DomainParticipantOptions\|ParticipantTransportSet.Create" tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs
```

- [ ] **Step 2: 機械的書き換え**

各 `new DomainParticipantOptions { ... }` を `new ContextOptions { ... }` に置換し、`ParticipantTransportSet.Create(new DomainParticipantOptions { ... })` を `ParticipantTransportSet.Create(new ContextOptions { ... })` に置換する。

ファイル先頭に `using ROSettaDDS.Rcl;` を追加。

例:

```csharp
// before
using var transports = ParticipantTransportSet.Create(new DomainParticipantOptions
{
    DomainId = 0,
    LocalhostOnly = true,
});

// after
using var transports = ParticipantTransportSet.Create(new ContextOptions
{
    DomainId = 0,
    LocalhostOnly = true,
});
```

- [ ] **Step 3: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ParticipantTransportSetTests"
```

期待: 6 件全件 PASS。

- [ ] **Step 4: コミット**

```bash
git add tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs
git commit -m "test(dds): ParticipantTransportSetTests を ContextOptions 起点に更新"
```

### Task 2.5: LeaseExpiryMonitorTests を ContextOptions 起点に

**Files:**
- Modify: `tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs`

- [ ] **Step 1: 既存テストの確認**

```bash
grep -n "DomainParticipantOptions" tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs
```

- [ ] **Step 2: 機械的書き換え**

`new DomainParticipantOptions { ... }` → `new ContextOptions { ... }`。`using ROSettaDDS.Rcl;` 追加。

- [ ] **Step 3: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~LeaseExpiryMonitorTests"
```

期待: 全件 PASS。

- [ ] **Step 4: コミット**

```bash
git add tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs
git commit -m "test(dds): LeaseExpiryMonitorTests を ContextOptions 起点に更新"
```

### Task 2.6: Phase 2 全体検証

- [ ] **Step 1: 全体テスト**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj
```

期待: 全件 PASS。`DomainParticipant` 周りの統合テストが緑のままなら Phase 2 完了。

- [ ] **Step 2: Phase 2 完了確認コミット (no-op なら skip)**

Phase 2 全体は複数コミットに分割済み。追加変更があればコミット。なければ次へ。

---

## Phase 3: Context クラス

> **目的:** DDS 資源 (transport / DiscoveryDb / SPDP / SEDP / LeaseExpiryMonitor / ParticipantRtpsReceiver) を所有する `Rcl.Context` を新設する。既存の `DomainParticipant` コンストラクタのロジック (Pub/Sub 部分を除く) をコピーする。

### Task 3.1: Context スケルトンと GuidPrefix 生成

**Files:**
- Create: `src/rosettadds/Rcl/Context.cs` (+ `.meta`)
- Test: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

- [ ] **Step 1: テスト作成**

`tests/rosettadds.Tests/Rcl/ContextTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class ContextTests
{
    [Fact]
    public void コンストラクタは_GuidPrefix_を生成する()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotEqual(default, ctx.GuidPrefix);
        Assert.Equal(new Guid(ctx.GuidPrefix, BuiltinEntityIds.Participant), ctx.Guid);
    }

    [Fact]
    public void コンストラクタは_options_を保持する()
    {
        var opts = new ContextOptions { DomainId = 42, LocalhostOnly = true, Logger = NullLogger.Instance };
        using var ctx = new Context(opts);
        Assert.Same(opts, ctx.Options);
    }

    [Fact]
    public void null_options_を渡すと_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Context(null!));
    }
}
```

注: `BuiltinEntityIds` の using が必要。`using ROSettaDDS.Discovery;` を追加すること。

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: FAIL (Context 未定義)。

- [ ] **Step 3: Context スケルトン実装**

`src/rosettadds/Rcl/Context.cs`:

```csharp
using System.Net;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_context_t 相当。ドメイン共通の DDS 資源を所有する。
/// 1 プロセス内で複数 Node をホストできる。
/// </summary>
public sealed class Context : IDisposable
{
    private readonly ContextOptions _options;
    private bool _started;
    private bool _disposed;

    public Context(ContextOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _options = options;

        GuidPrefix = GuidPrefix.CreateForCurrentProcess(_options.VendorId);
        Guid = new Guid(GuidPrefix, BuiltinEntityIds.Participant);
    }

    public GuidPrefix GuidPrefix { get; }
    public Guid Guid { get; }
    public ContextOptions Options => _options;
    public ILogger Logger => _options.Logger;

    public int ResolvedParticipantId => throw new NotImplementedException();

    public IRtpsTransport UserMulticastTransport => throw new NotImplementedException();
    public IRtpsTransport UserUnicastTransport => throw new NotImplementedException();
    public Locator UserMulticastDestination => throw new NotImplementedException();
    public DiscoveryDb DiscoveryDb => throw new NotImplementedException();

    public void Start() => throw new NotImplementedException();
    public void Stop() => throw new NotImplementedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
```

- [ ] **Step 4: Unity .meta 生成**

`src/rosettadds/Rcl/Context.cs.meta` を `ContextOptions.cs.meta` と同形式で。

- [ ] **Step 5: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 3 件 PASS。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Rcl/Context.cs src/rosettadds/Rcl/Context.cs.meta tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "feat(rcl): Context スケルトン (GuidPrefix 生成 + Dispose 骨格)"
```

### Task 3.2: Context に transport 4 種 + DiscoveryDb + SPDP/SEDP を移植

**Files:**
- Modify: `src/rosettadds/Rcl/Context.cs`
- Modify: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

- [ ] **Step 1: テスト追加 (transport と DiscoveryDb の有無)**

`ContextTests.cs` に追記:

```csharp
    [Fact]
    public void コンストラクタ完了時に_transport_4_種と_DiscoveryDb_が利用可能な状態になる()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        // Start 前は transport を内部で作っているが、外から見える形にしていないので
        // ResolvedParticipantId / DiscoveryDb が NotImplementedException にならないことだけ確認。
        Assert.NotEqual(0, ctx.ResolvedParticipantId);  // transport probe が走るので 0 以外
        Assert.NotNull(ctx.DiscoveryDb);
    }

    [Fact]
    public void UserMulticast_と_UserUnicast_と_Destination_が_context_から取得できる()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotNull(ctx.UserMulticastTransport);
        Assert.NotNull(ctx.UserUnicastTransport);
        Assert.NotEqual(Locator.Invalid, ctx.UserMulticastDestination);
    }
```

注: `Locator` の using が必要 (`using ROSettaDDS.Rtps;` 既にあるはず)。

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 新規 2 件が NotImplementedException で FAIL。

- [ ] **Step 3: DomainParticipant の transport 関連ロジックを Context へ移植**

`src/rosettadds/Dds/DomainParticipant.cs:60-135` のうち、transport / DiscoveryDb / SPDP / SEDP / LeaseExpiryMonitor / ParticipantRtpsReceiver 周りのコードを `Context.cs` のコンストラクタにコピーする。

Context.cs へ追加する using:

```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.Writer;
```

Context.cs へ追加する private フィールド:

```csharp
    private readonly ParticipantTransportSet _transports;
    private readonly ParticipantRtpsReceiver _receiver;
    private readonly DiscoveryDb _discoveryDb;
    private readonly LeaseExpiryMonitor _leaseExpiryMonitor;
    private readonly SpdpBuiltinParticipantReader _spdpReader;
    private readonly SpdpBuiltinParticipantWriter _spdpWriter;
    private readonly SedpEndpointWriter _sedpPublicationsWriter;
    private readonly SedpEndpointReader _sedpPublicationsReader;
    private readonly SedpEndpointWriter _sedpSubscriptionsWriter;
    private readonly SedpEndpointReader _sedpSubscriptionsReader;
    private readonly SedpEndpointAdvertiser _sedpAdvertiser;
```

Context コンストラクタの `Guid` 構築直後に追加:

```csharp
        _transports = ParticipantTransportSet.Create(_options);
        _receiver = new ParticipantRtpsReceiver(GuidPrefix, _options.Logger);

        _discoveryDb = new DiscoveryDb(_options.DiscoveryLimits);
        _leaseExpiryMonitor = new LeaseExpiryMonitor(_discoveryDb, _options, _options.Logger);

        _spdpReader = new SpdpBuiltinParticipantReader(
            _transports.MetatrafficMulticast, _discoveryDb, GuidPrefix, _options.Logger, limits: _options.DiscoveryLimits);

        _spdpWriter = new SpdpBuiltinParticipantWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            participantDataProvider: BuildParticipantData,
            interval: _options.SpdpInterval,
            logger: _options.Logger);

        _sedpPublicationsWriter = new SedpEndpointWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpPublicationsReader = new SedpEndpointReader(
            replyTransport: _transports.MetatrafficUnicast,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinPublicationsReader,
            ackNackFallbackDestination: _transports.MetatrafficMulticastDestination,
            producedEndpointKind: EndpointKind.Writer,
            logger: _options.Logger,
            limits: _options.DiscoveryLimits);

        _sedpSubscriptionsWriter = new SedpEndpointWriter(
            transport: _transports.MetatrafficMulticast,
            multicastDestination: _transports.MetatrafficMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsWriter,
            heartbeatPeriod: _options.SedpInterval,
            logger: _options.Logger);

        _sedpSubscriptionsReader = new SedpEndpointReader(
            replyTransport: _transports.MetatrafficUnicast,
            discoveryDb: _discoveryDb,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: BuiltinEntityIds.SedpBuiltinSubscriptionsReader,
            ackNackFallbackDestination: _transports.MetatrafficMulticastDestination,
            producedEndpointKind: EndpointKind.Reader,
            logger: _options.Logger,
            limits: _options.DiscoveryLimits);

        _sedpAdvertiser = new SedpEndpointAdvertiser(
            _options.Logger,
            () => _leaseExpiryMonitor.CancellationToken,
            () => _disposed);

        // builtin endpoint を単一 receiver のルーティング対象として登録する。
        _receiver.RegisterReader(BuiltinEntityIds.SpdpBuiltinParticipantReader, _spdpReader);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinPublicationsReader, _sedpPublicationsReader.Stateful);
        _receiver.RegisterReader(BuiltinEntityIds.SedpBuiltinSubscriptionsReader, _sedpSubscriptionsReader.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinPublicationsWriter, _sedpPublicationsWriter.Stateful);
        _receiver.RegisterWriter(BuiltinEntityIds.SedpBuiltinSubscriptionsWriter, _sedpSubscriptionsWriter.Stateful);
```

`throw new NotImplementedException()` を実プロパティに置き換え:

```csharp
    public int ResolvedParticipantId => _transports.ResolvedParticipantId;
    public IRtpsTransport UserMulticastTransport => _transports.UserMulticast;
    public IRtpsTransport UserUnicastTransport => _transports.UserUnicast;
    public Locator UserMulticastDestination => _transports.UserMulticastDestination;
    public DiscoveryDb DiscoveryDb => _discoveryDb;
```

`BuildParticipantData` を Context に移植 (DomainParticipant.cs:269-286):

```csharp
    /// <summary>現在の自 Participant の <see cref="ParticipantData"/> を生成する (SPDP 送信時に使われる)。</summary>
    public ParticipantData BuildParticipantData()
    {
        var data = new ParticipantData
        {
            ProtocolVersion = _options.ProtocolVersion,
            VendorId = _options.VendorId,
            Guid = Guid,
            BuiltinEndpoints = BuiltinEndpointSet.ROSettaDDSDefault,
            LeaseDuration = _options.LeaseDuration,
            EntityName = _options.EntityName,
        };
        data.MetatrafficMulticastLocators.Add(_transports.MetatrafficMulticastDestination);
        data.MetatrafficUnicastLocators.AddRange(_transports.MetatrafficUnicastLocators);
        data.DefaultUnicastLocators.AddRange(_transports.DefaultUnicastLocators);
        data.DefaultMulticastLocators.Add(_transports.UserMulticastDestination);
        return data;
    }
```

- [ ] **Step 4: ビルド確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
```

期待: 0 エラー。

- [ ] **Step 5: Context テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 5 件全件 PASS。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "feat(rcl): Context に transport 4 種 / DiscoveryDb / SPDP / SEDP を移植"
```

### Task 3.3: Context.Start / Stop / Dispose 実装

**Files:**
- Modify: `src/rosettadds/Rcl/Context.cs`
- Modify: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

- [ ] **Step 1: テスト追加 (lifecycle)**

`ContextTests.cs` に追記:

```csharp
    [Fact]
    public void Start_Stop_は_冪等()
    {
        using var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Start();
        ctx.Start();  // 二度呼んでも例外なし
        ctx.Stop();
        ctx.Stop();   // 二度呼んでも例外なし
    }

    [Fact]
    public void Dispose_後は_Start_が_ObjectDisposedException()
    {
        var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ctx.Start());
    }

    [Fact]
    public void Dispose_後は_Stop_が_ObjectDisposedException()
    {
        var ctx = new Context(new ContextOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ctx.Stop());
    }
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 新規 3 件が NotImplementedException で FAIL。

- [ ] **Step 3: Start / Stop / Dispose 実装**

`Context.cs` の `throw new NotImplementedException()` を以下に置換:

```csharp
    public void Start()
    {
        ThrowIfDisposed();
        if (_started) return;
        _transports.Start();

        // participant 単位の単一 receiver が全 transport の受信を 1 経路に集約する。
        _receiver.Subscribe(_transports.MetatrafficMulticast);
        _receiver.Subscribe(_transports.MetatrafficUnicast);
        _receiver.Subscribe(_transports.UserMulticast);
        _receiver.Subscribe(_transports.UserUnicast);

        _spdpWriter.Start();
        _sedpPublicationsWriter.Start();
        _sedpSubscriptionsWriter.Start();
        _leaseExpiryMonitor.Start();
        _started = true;
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (!_started) return;
        _leaseExpiryMonitor.Stop();
        _sedpPublicationsWriter.Stop();
        _sedpSubscriptionsWriter.Stop();
        _receiver.UnsubscribeAll();
        _spdpWriter.Stop();
        _transports.Stop();
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _sedpPublicationsWriter.Dispose();
        _sedpSubscriptionsWriter.Dispose();
        _sedpPublicationsReader.Dispose();
        _sedpSubscriptionsReader.Dispose();
        _spdpWriter.Dispose();
        _spdpReader.Dispose();
        _leaseExpiryMonitor.Dispose();
        _receiver.Dispose();
        _transports.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
```

注: `using ROSettaDDS.Discovery;` (DiscoveryDb / LeaseExpiryMonitor / SPDP / SEDP) と `using ROSettaDDS.Rtps;` (ParticipantRtpsReceiver / Locator) が必要。

- [ ] **Step 4: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 8 件全件 PASS。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "feat(rcl): Context.Start/Stop/Dispose 実装 (冪等性 + Dispose 後例外)"
```

### Task 3.4: Discovery イベントハンドラを Context へ移植

**Files:**
- Modify: `src/rosettadds/Rcl/Context.cs`

- [ ] **Step 1: 確認 (DomainParticipant.cs:142-218 のイベントハンドラ群)**

`OnRemoteParticipantDiscovered` / `OnRemoteParticipantLost` / `OnRemoteReaderDiscovered` / `OnRemoteWriterDiscovered` / `OnRemoteEndpointUpdated` / `OnRemoteReaderLost` / `OnRemoteWriterLost` は **Pub/Sub 関係だが、Pub/Sub は Node 側に移す予定**。このタスクでは **イベント購読と auto-match だけ** Context 側に置き、Pub/Sub 連携ハンドラ (`OnRemoteReader*` / `OnRemoteWriter*`) は Node 側に移譲する形にする。

- [ ] **Step 2: Context コンストラクタ末尾に Discovery イベント購読を追加**

GuidPrefix 生成直後、build の前に:

```csharp
        // SPDP で remote participant を発見/更新したら SEDP endpoint を auto-match
        _discoveryDb.ParticipantDiscovered += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantUpdated += OnRemoteParticipantDiscovered;
        _discoveryDb.ParticipantLost += OnRemoteParticipantLost;
```

`Guid` 構築直後に配置 (DomainParticipant.cs:142-145 と同じ)。

- [ ] **Step 3: OnRemoteParticipantDiscovered / OnRemoteParticipantLost を Context に移植**

`DomainParticipant.cs:166-200` の該当メソッドを `Context.cs` にコピーする。

`internal void` ではなく `private void` でよい (外部から呼ばれない)。Node 側が `Context` の SEDP writer へアクセスする必要がある場合は、後の Phase 4 で internal メソッドを生やす。

- [ ] **Step 4: Pub/Sub 関連ハンドラは一旦 stub**

`_discoveryDb.ReaderDiscovered += OnRemoteReaderDiscovered;` などは **後段 (Phase 4)** で Node 側に移す。**このタスクでは購読自体を Context に置かない**。

代わりに、Phase 4 で Node 側が Context の `DiscoveryDb` を見て `Remote*` イベントを購読する形にする。

- [ ] **Step 5: ビルド確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
```

期待: 0 エラー。

- [ ] **Step 6: 既存 Integration テスト確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Integration"
```

期待: **Pub/Sub 系の integration テストは失敗する** (Node 側に移す予定の ReaderDiscovered / WriterDiscovered イベントが Context に繋がっていないため)。これは想定内。**Discovery 系の integration テスト (SpdpLoopbackTests) は緑のまま**。

- [ ] **Step 7: コミット**

```bash
git add src/rosettadds/Rcl/Context.cs
git commit -m "feat(rcl): Context に SPDP / SEDP イベント購読と auto-match を移植"
```

注: 既存 `DomainParticipant` 側のイベントハンドラは **まだ残す** (このタスクでは Context 側に追加しただけ)。Phase 5 で DomainParticipant 全体をリファクタする。

### Task 3.5: Context 寿命管理 (Node の tracking)

> **目的:** spec の「Node の寿命 > Context の寿命は許さない」を保証するため、Context が Node リストを持ち、`Context.Dispose` 時に生存 Node を先に Dispose する。

**Files:**
- Modify: `src/rosettadds/Rcl/Context.cs`
- Modify: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

- [ ] **Step 1: テスト追加**

`ContextTests.cs` に追記:

```csharp
    [Fact]
    public void Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する()
    {
        // まだ Node.cs が存在しないのでコンパイルできない。
        // この時点ではこのテストはコメントアウトし、Task 4.5 で有効化する。
        // [Fact]
        // public void Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する()
        // {
        //     var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        //     var node = new Node(ctx, "test");
        //     ctx.Dispose();
        //     Assert.Throws<ObjectDisposedException>(() => node.CreatePublisher<StringMessage>(
        //         "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
        // }
    }
```

注: `Node` クラスがまだ未定義なのでテスト本体はコメントアウト。Task 4.5 で実装完了後に有効化する。

- [ ] **Step 2: Context に Node 追跡機能を追加**

`Context.cs` の `private readonly List<Node> _nodes = new();` をフィールドに追加。`internal` メソッドを追加:

```csharp
    private readonly List<Node> _nodes = new();
    private readonly object _nodesLock = new();

    internal void RegisterNode(Node node)
    {
        ThrowIfDisposed();
        lock (_nodesLock) _nodes.Add(node);
    }

    internal void UnregisterNode(Node node)
    {
        lock (_nodesLock) _nodes.Remove(node);
    }

    private void DisposeTrackedNodes()
    {
        Node[] snapshot;
        lock (_nodesLock) snapshot = _nodes.ToArray();
        foreach (var node in snapshot)
        {
            try { node.Dispose(); }
            catch (Exception ex) { _options.Logger.Warn($"Context.Dispose failed to dispose Node: {ex.Message}", ex); }
        }
    }
```

- [ ] **Step 3: Context.Dispose の先頭で `DisposeTrackedNodes` を呼ぶ**

`Dispose()` メソッドを以下に修正:

```csharp
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTrackedNodes();
        Stop();
        _sedpPublicationsWriter.Dispose();
        _sedpSubscriptionsWriter.Dispose();
        _sedpPublicationsReader.Dispose();
        _sedpSubscriptionsReader.Dispose();
        _spdpWriter.Dispose();
        _spdpReader.Dispose();
        _leaseExpiryMonitor.Dispose();
        _receiver.Dispose();
        _transports.Dispose();
    }
```

- [ ] **Step 4: ビルド + テスト通過確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 8 件 PASS。`Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する` はコメントアウト中なので 9 件目のテストは出ない (テストカウントは同じ)。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "feat(rcl): Context に Node 追跡機能を追加 (Dispose 時の安全保証)"
```

注: Task 4.1 で Node.cs 側に `RegisterNode` / `UnregisterNode` の呼び出しを追加する。

### Task 3.6: Phase 3 確認

- [ ] **Step 1: Context テスト緑**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Rcl"
```

期待: 8 件 (Context) + 3 件 (NodeOptions) + 2 件 (ContextOptions) 全件 PASS。

- [ ] **Step 2: DomainParticipant はまだ動かないことを確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Integration.PubSub"
```

期待: 失敗する。Phase 4 + Phase 5 で直す。

---

## Phase 4: Node クラス

> **目的:** `Rcl.Node` を新設し、Pub/Sub/Client の user endpoint 集合 (`UserEndpointManager` + `ParticipantEndpointFactory`) を所有する。`Context` を参照して `Context.DiscoveryDb` の `Remote*` イベントを購読する。

### Task 4.1: Node スケルトン

**Files:**
- Create: `src/rosettadds/Rcl/Node.cs` (+ `.meta`)
- Test: `tests/rosettadds.Tests/Rcl/NodeTests.cs`

- [ ] **Step 1: テスト作成**

`tests/rosettadds.Tests/Rcl/NodeTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using ROSettaDDS.Rcl;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

public class NodeTests
{
    [Fact]
    public void コンストラクタは_Context_参照と_Name_を保持する()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        using var node = new Node(ctx, "chatter_talker");

        Assert.Same(ctx, node.Context);
        Assert.Equal("chatter_talker", node.Name);
    }

    [Fact]
    public void null_context_を渡すと_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Node(null!, "name"));
    }

    [Fact]
    public void null_name_を渡すと_ArgumentException()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        Assert.Throws<ArgumentException>(() => new Node(ctx, null!));
    }
}
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeTests"
```

期待: FAIL (Node 未定義)。

- [ ] **Step 3: Context に必要な internal API を追加**

`Context.cs` の `public sealed class Context` 内の末尾に以下を追加 (次の `Step 4` で Node から参照される):

```csharp
    // ----- Node からの借用口 (internal) -----

    internal ParticipantTransportSet Transports => _transports;
    internal ParticipantRtpsReceiver Receiver => _receiver;
    internal CancellationToken LeaseExpiryCancellationToken => _leaseExpiryMonitor.CancellationToken;
```

- [ ] **Step 4: Node スケルトン実装**

`src/rosettadds/Rcl/Node.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Rcl;

/// <summary>
/// ROS 2 の rcl_node_t (rclcpp::Node) 相当。<see cref="Context"/> を参照し、
/// Publisher / Subscription / ServiceClient のみを生やす薄いラッパ。
/// </summary>
public sealed class Node : IDisposable
{
    private readonly NodeOptions _options;
    private readonly UserEntityIdAllocator _userEntityIds = new();
    private readonly ParticipantEndpointFactory _endpointFactory;
    private readonly UserEndpointManager _userEndpoints;
    private readonly SedpEndpointAdvertiser _sedpAdvertiser;
    private bool _disposed;

    public Node(Context context, string name, NodeOptions? options = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Value cannot be null or empty.", nameof(name));
        Context = context;
        Name = name;
        _options = options ?? NodeOptions.Default;

        _endpointFactory = new ParticipantEndpointFactory(
            context.Options,
            context.Transports,
            context.GuidPrefix,
            context.Guid,
            _userEntityIds);

        _userEndpoints = new UserEndpointManager(
            context.DiscoveryDb,
            new ParticipantRtpsReceiverAdapter(context.Receiver),
            context.Logger);

        _sedpAdvertiser = new SedpEndpointAdvertiser(
            context.Logger,
            () => context.LeaseExpiryCancellationToken,
            () => _disposed);

        // Context に自身を登録し、Context.Dispose 時に先に Dispose されるようにする。
        context.RegisterNode(this);
    }

    public string Name { get; }
    public Context Context { get; }
    public NodeOptions Options => _options;

    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => throw new NotImplementedException();

    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
        => throw new NotImplementedException();

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => throw new NotImplementedException();

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => throw new NotImplementedException();

    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
        => throw new NotImplementedException();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Context.UnregisterNode(this);
    }
}
```

- [ ] **Step 5: Unity .meta 生成**

`src/rosettadds/Rcl/Node.cs.meta` を `Context.cs.meta` と同形式で。

- [ ] **Step 6: ビルド + テスト通過確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeTests"
```

期待: ビルド成功、3 件 PASS。

- [ ] **Step 7: コミット**

```bash
git add src/rosettadds/Rcl/Node.cs src/rosettadds/Rcl/Node.cs.meta src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/NodeTests.cs
git commit -m "feat(rcl): Node スケルトン (Context 借用口で endpoint factory 構築)"
```

注: `Context.cs` も一緒にコミットされるのは、Step 3 で追加した `Transports` / `Receiver` / `LeaseExpiryCancellationToken` の internal プロパティが Node コンストラクタから呼ばれるため。

### Task 4.2: Node.CreatePublisher / CreateSubscription 実装

**Files:**
- Modify: `src/rosettadds/Rcl/Node.cs`
- Modify: `tests/rosettadds.Tests/Rcl/NodeTests.cs`

- [ ] **Step 1: テスト追加 (Pub/Sub 正常系)**

`NodeTests.cs` に追記:

```csharp
    [Fact]
    public void CreatePublisher_が_Publisher_を返し_Dispose_後に例外を投げる()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "talker");
        try
        {
            using var pub = node.CreatePublisher<StringMessage>(
                "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
            Assert.NotNull(pub);
        }
        finally { node.Dispose(); }

        Assert.Throws<ObjectDisposedException>(() =>
            node.CreatePublisher<StringMessage>("chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
    }

    [Fact]
    public void CreateSubscription_が_Subscription_を返す()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var node = new Node(ctx, "listener");
        using var sub = node.CreateSubscription<StringMessage>(
            "chatter",
            StringMessageSerializer.Instance,
            (msg) => { });
        Assert.NotNull(sub);
    }
```

注: `StringMessage` を使うため `using ROSettaDDS.Msgs.Std;` が必要。`Publisher` / `Subscription` の `Start()` はコンストラクタ (or 初期化) で呼ばれるため、Node 側で `StartWriters` のような明示呼び出しは不要。

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeTests"
```

期待: 2 件が NotImplementedException で FAIL。

- [ ] **Step 3: Create* 実装**

`Node.cs` の `throw new NotImplementedException()` を `DomainParticipant.cs:293-411` のロジックを移植する形に書き換え。

`CreateWriterInternal` (private):

```csharp
    private Publisher<T> CreateWriterInternal<T>(
        string ddsTopic,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName,
        string userTopicName)
    {
        var endpoint = _endpointFactory.CreateWriter(ddsTopic, serializer, reliability, durability, typeName);
        var writer = endpoint.Writer;
        var endpointData = endpoint.EndpointData;
        _userEndpoints.RegisterWriter(endpointData, writer);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddPublicationAsync(endpointData, token),
            "Node failed to advertise local writer endpoint");

        var pub = new Publisher<T>(userTopicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
    }

    private ReliableUserReader CreateReliableReplyReaderInternal(string ddsTopic, string ddsTypeName)
    {
        var endpoint = _endpointFactory.CreateReliableReplyReader(ddsTopic, ddsTypeName);
        var reader = endpoint.Reader;
        var endpointData = endpoint.EndpointData;

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddSubscriptionAsync(endpointData, token),
            "Node failed to advertise local service reply reader endpoint");
        return reader;
    }
```

`CreatePublisher` (public 2 overload):

```csharp
    public Publisher<T> CreatePublisher<T>(string topicName, ICdrSerializer<T> serializer, string? typeName = null)
        => CreatePublisher(topicName, serializer, ReliabilityQos.Reliable, DurabilityQos.Volatile, typeName);

    public Publisher<T> CreatePublisher<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string? typeName = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        return CreateWriterInternal(
            TopicNameMangler.MangleTopic(topicName), serializer, reliability, durability,
            typeName, topicName);
    }
```

`CreateSubscription` (public 2 overload):

```csharp
    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T, GuidPrefix> handler,
        string? typeName = null,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Value cannot be null or empty.", nameof(topicName));
        if (serializer is null) throw new ArgumentNullException(nameof(serializer));
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var effectiveReliability = reliability ?? ReliabilityQos.Reliable;
        var ddsTopic = TopicNameMangler.MangleTopic(topicName);
        var endpoint = _endpointFactory.CreateReader(ddsTopic, serializer, effectiveReliability, typeName);
        var reader = endpoint.Reader;
        var endpointGuid = endpoint.EndpointGuid;
        var endpointData = endpoint.EndpointData;

        var subscription = new Subscription<T>(
            topicName,
            endpointGuid,
            reader,
            serializer,
            handler,
            UnregisterLocalReader,
            handlerContext,
            Context.Logger,
            cdrReadLimits: Context.Options.CdrReadLimits);

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = _sedpAdvertiser.RunAsync(
            token => Context.AddSubscriptionAsync(endpointData, token),
            "Node failed to advertise local reader endpoint");

        return subscription;
    }

    public Subscription<T> CreateSubscription<T>(
        string topicName,
        ICdrSerializer<T> serializer,
        Action<T> handler,
        SynchronizationContext? handlerContext = null,
        ReliabilityQos? reliability = null)
        => CreateSubscription<T>(
            topicName,
            serializer,
            (value, _) => handler(value),
            handlerContext: handlerContext,
            reliability: reliability);
```

`CreateServiceClient` (public 1 overload):

```csharp
    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
    {
        ThrowIfDisposed();
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Value cannot be null or empty.", nameof(serviceName));

        var requestPublisher = CreateWriterInternal(
            TopicNameMangler.MangleServiceRequest(serviceName),
            descriptor.RequestSerializer,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile,
            typeName: descriptor.RequestDdsTypeName,
            userTopicName: serviceName);

        var replyReader = CreateReliableReplyReaderInternal(
            TopicNameMangler.MangleServiceReply(serviceName),
            descriptor.ResponseDdsTypeName);

        return new ServiceClient<TRequest, TResponse>(
            requestPublisher, replyReader, descriptor, Context.Logger, Context.Options.CdrReadLimits);
    }
```

`Dispose` 実装:

```csharp
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAllLocalEndpoints();
    }

    private void UnregisterAllLocalEndpoints()
    {
        var endpoints = _userEndpoints.Snapshot();
        foreach (var writer in endpoints.Writers)
        {
            UnregisterLocalWriter(writer.Guid, writer);
            writer.Dispose();
        }
        foreach (var reader in endpoints.Readers)
        {
            var readerGuid = new Guid(Context.GuidPrefix, reader.ReaderEntityId);
            UnregisterLocalReader(readerGuid, reader);
            reader.Dispose();
        }
    }

    private void UnregisterLocalWriter(Guid endpointGuid, StatefulWriter writerToRemove)
    {
        var result = _userEndpoints.UnregisterWriter(endpointGuid, writerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(Context.UnregisterPublicationAsync(result.Endpoint!));
        }
    }

    private void UnregisterLocalReader(Guid endpointGuid, IUserReader readerToRemove)
    {
        var result = _userEndpoints.UnregisterReader(endpointGuid, readerToRemove);
        if (result.ShouldAdvertise)
        {
            _sedpAdvertiser.WaitForUnregister(Context.UnregisterSubscriptionAsync(result.Endpoint!));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
```

- [ ] **Step 4: Context に必要な internal API を追加**

`Context.cs` の末尾に SEDP 広告メソッドを追加:

```csharp
    // ----- SEDP 広告の Node 向け delegate -----

    internal ValueTask AddPublicationAsync(DiscoveredEndpointData endpointData, CancellationToken token)
        => _sedpPublicationsWriter.AddEndpointAsync(endpointData, token);

    internal ValueTask AddSubscriptionAsync(DiscoveredEndpointData endpointData, CancellationToken token)
        => _sedpSubscriptionsWriter.AddEndpointAsync(endpointData, token);

    internal ValueTask UnregisterPublicationAsync(DiscoveredEndpointData endpoint)
        => _sedpPublicationsWriter.UnregisterEndpointAsync(endpoint);

    internal ValueTask UnregisterSubscriptionAsync(DiscoveredEndpointData endpoint)
        => _sedpSubscriptionsWriter.UnregisterEndpointAsync(endpoint);
```

- [ ] **Step 5: ビルド確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
```

期待: 0 エラー。`DiscoveredEndpointData` の using (`using ROSettaDDS.Discovery;`) が必要なら追加。

- [ ] **Step 6: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeTests"
```

期待: 5 件全件 PASS。

- [ ] **Step 7: コミット**

```bash
git add src/rosettadds/Rcl/Node.cs src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/NodeTests.cs
git commit -m "feat(rcl): Node.CreatePublisher / CreateSubscription / CreateServiceClient 実装"
```

### Task 4.3: Node の Discovery イベント購読

**Files:**
- Modify: `src/rosettadds/Rcl/Node.cs`

- [ ] **Step 1: Node コンストラクタ末尾に Discovery イベント購読を追加**

`ParticipantEndpointFactory` 構築の後、_sedpAdvertiser 構築の前に:

```csharp
        // Context の DiscoveryDb の Remote* イベントを購読し、user endpoint の
        // マッチ・アンマッチを UserEndpointManager へ伝搬する。
        var discovery = context.DiscoveryDb;
        discovery.ReaderDiscovered += OnRemoteReaderDiscovered;
        discovery.WriterDiscovered += OnRemoteWriterDiscovered;
        discovery.EndpointUpdated += OnRemoteEndpointUpdated;
        discovery.ReaderLost += OnRemoteReaderLost;
        discovery.WriterLost += OnRemoteWriterLost;
```

- [ ] **Step 2: ハンドラを移植**

`DomainParticipant.cs:202-224` の `OnRemoteReader*` / `OnRemoteWriter*` / `OnRemoteEndpointUpdated` を `Node.cs` に移植。`RemoteReaderChanged` / `RemoteWriterChanged` / `RemoteReaderLost` / `RemoteWriterLost` のシグネチャは `UserEndpointManager` と同じ。

```csharp
    private void OnRemoteReaderDiscovered(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderChanged(remoteReader);

    private void OnRemoteWriterDiscovered(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterChanged(remoteWriter);

    private void OnRemoteEndpointUpdated(RemoteEndpoint remoteEndpoint)
    {
        if (remoteEndpoint.Kind == EndpointKind.Reader)
            OnRemoteReaderDiscovered(remoteEndpoint);
        else
            OnRemoteWriterDiscovered(remoteEndpoint);
    }

    private void OnRemoteReaderLost(RemoteEndpoint remoteReader)
        => _userEndpoints.RemoteReaderLost(remoteReader);

    private void OnRemoteWriterLost(RemoteEndpoint remoteWriter)
        => _userEndpoints.RemoteWriterLost(remoteWriter);
```

`RemoteEndpoint` / `EndpointKind` の using が必要 (`using ROSettaDDS.Discovery;`)。

- [ ] **Step 3: Pub/Sub 動作の Node テスト (1 topic × 1 ctx に Node 2 つ)**

`NodeTests.cs` に追記:

```csharp
    [Fact]
    public async Task 同一_Context_上の_2_Node_で_Pub_Sub_できる()
    {
        using var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        ctx.Start();
        using var talker = new Node(ctx, "talker");
        using var listener = new Node(ctx, "listener");

        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var sub = listener.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (msg) => received.Enqueue(msg.Data),
            StringMessage.DdsTypeName);
        using var pub = talker.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);

        // マッチ待ち (Subscription の WaitForMatchedAsync を使う)
        await sub.WaitForMatchedAsync(1, TimeSpan.FromSeconds(5));

        // 配信
        pub.Publish(new StringMessage("hello"));

        // 受信待ち (タイムアウト 3 秒)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.False(received.IsEmpty);
        Assert.Equal("hello", received.First());
    }
```

注: `Subscription.WaitForMatchedAsync` は既存の `docs/superpowers/specs/2026-06-19-wait-for-matched-api-design.md` で導入された API。`tests/rosettadds.Tests/Integration/PublisherSubscriptionMatchedTests.cs` を参考にする。`pub.Publish` は同期 API (sample 受信後の ack 待ちなし)。テスト本体を `async Task` にする必要があるので、`NodeTests` クラスに `using System.Threading.Tasks;` を追加し、テストメソッドのシグネチャを `public async Task` にする。

- [ ] **Step 4: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~NodeTests"
```

期待: 6 件全件 PASS。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rcl/Node.cs tests/rosettadds.Tests/Rcl/NodeTests.cs
git commit -m "feat(rcl): Node に Discovery イベント購読と Pub/Sub 動作テスト追加"
```

### Task 4.4: Context_Dispose 時に生存 Node を先に Dispose するテストを有効化

**Files:**
- Modify: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

- [ ] **Step 1: コメントアウトしていたテストを有効化**

`ContextTests.cs` の `Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する` の外側コメント `[Fact] public void ...` を解除する。

最終形:

```csharp
    [Fact]
    public void Context_Dispose_時に_生存中の_Node_を_先に_Dispose_する()
    {
        var ctx = new Context(new ContextOptions { LocalhostOnly = true, Logger = NullLogger.Instance });
        var node = new Node(ctx, "test");
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => node.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName));
    }
```

- [ ] **Step 2: テスト通過確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests"
```

期待: 9 件全件 PASS (うち 1 件が新テスト)。

- [ ] **Step 3: コミット**

```bash
git add tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "test(rcl): Context_Dispose 時の Node 自動 Dispose テストを有効化"
```

### Task 4.5: Phase 4 確認

- [ ] **Step 1: Node + Context テスト緑**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Rcl"
```

期待: 全件 PASS。

---

## Phase 5: DomainParticipant を Context + Node 委譲パターンに書き換え

> **目的:** 既存 `DomainParticipant` の中身を削除し、`Rcl.Context` + `Rcl.Node` を内部生成するだけの薄いラッパにする。`Create*` メソッドに `[Obsolete]` を付与する。既存テスト 60+ ファイルは変更せず、Obsolete 警告は出てもビルドは緑であることを確認する。

### Task 5.1: DomainParticipantObsoleteTests 新規作成

**Files:**
- Create: `tests/rosettadds.Tests/Rcl/DomainParticipantObsoleteTests.cs`

- [ ] **Step 1: テスト作成**

`tests/rosettadds.Tests/Rcl/DomainParticipantObsoleteTests.cs`:

```csharp
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.Std;
using Xunit;

namespace ROSettaDDS.Tests.Rcl;

// DomainParticipant は Obsolete なのでコンパイル警告を抑止する。
#pragma warning disable CS0618

public class DomainParticipantObsoleteTests
{
    [Fact]
    public void コンストラクタで_Context_と_Node_が生成される()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });

        Assert.NotEqual(default, dp.Guid);
        Assert.NotEqual(default, dp.GuidPrefix);
    }

    [Fact]
    public void CreatePublisher_が_obsolete_でも動く()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Start();

        using var pub = dp.CreatePublisher<StringMessage>(
            "chatter", StringMessageSerializer.Instance, StringMessage.DdsTypeName);
        Assert.NotNull(pub);
    }

    [Fact]
    public void CreateSubscription_が_obsolete_でも動く()
    {
        using var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Start();

        using var sub = dp.CreateSubscription<StringMessage>(
            "chatter", StringMessageSerializer.Instance,
            (msg) => { },
            StringMessage.DdsTypeName);
        Assert.NotNull(sub);
    }

    [Fact]
    public void Dispose_が_Context_と_Node_を_両方解放する()
    {
        var dp = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            Logger = NullLogger.Instance,
        });
        dp.Dispose();

        // 2 度 Dispose しても例外なし
        dp.Dispose();
    }
}

#pragma warning restore CS0618
```

- [ ] **Step 2: テスト失敗確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~DomainParticipantObsoleteTests"
```

期待: 1-3 件目がまだ既存 DomainParticipant 実装で動くので **PASS**。4 件目も既存実装で緑。

**このステップは「テストを書き、現行実装で緑になることを確認する」だけ**。書き換えは次のタスク。

- [ ] **Step 3: コミット**

```bash
git add tests/rosettadds.Tests/Rcl/DomainParticipantObsoleteTests.cs
git commit -m "test(rcl): DomainParticipant 互換 smoke test を追加 (現行実装で緑)"
```

### Task 5.2: DomainParticipant を Context + Node 委譲パターンに書き換え

**Files:**
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`

- [ ] **Step 1: 既存 DomainParticipant.cs を完全置換**

`src/rosettadds/Dds/DomainParticipant.cs` を以下の内容で **完全置換** (約 90 行):

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rcl;
using ROSettaDDS.Rtps;

namespace ROSettaDDS.Dds;

/// <summary>
/// ROSettaDDS の Domain Participant (旧公開エントリポイント)。
/// 内部では <see cref="Rcl.Context"/> + <see cref="Rcl.Node"/> を生成して委譲する。
/// 新規コードでは <see cref="Rcl.Context"/> + <see cref="Rcl.Node"/> を使うこと。
/// </summary>
[Obsolete("Use ROSettaDDS.Rcl.Context + ROSettaDDS.Rcl.Node instead. " +
          "DomainParticipant will be removed in a future release.")]
public sealed class DomainParticipant : IDisposable
{
    private readonly DomainParticipantOptions _options;
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
    public DiscoveryDb DiscoveryDb => _context.DiscoveryDb;
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

- [ ] **Step 2: ビルド確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
```

期待: 0 エラー。**Obsolete 警告が大量に出るが、** コード自体はビルドできる。

- [ ] **Step 3: DomainParticipantObsolete テスト**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~DomainParticipantObsoleteTests"
```

期待: 4 件全件 PASS (警告は出てもテストは緑)。

- [ ] **Step 4: 既存 Integration テスト**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Integration"
```

期待: 既存 integration テストが緑のまま (DomainParticipant 経由のものが、新しい Context+Node 委譲でも同じ動作をすること)。

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Dds/DomainParticipant.cs
git commit -m "refactor(dds): DomainParticipant を Rcl.Context + Rcl.Node への委譲に書き換え ([Obsolete] 付与)"
```

### Task 5.3: DomainParticipantOptions.cs の Cs0618 警告抑止 (テストプロジェクト)

- [ ] **Step 1: テストプロジェクト全体で Obsolete 警告を許可する設定か確認**

`tests/rosettadds.Tests/rosettadds.Tests.csproj` を確認し、`<TreatWarningsAsErrors>` や `<NoWarn>` の設定を見る。

```bash
cat tests/rosettadds.Tests/rosettadds.Tests.csproj
```

期待: `<NoWarn>` がない / もしくは CS0618 が含まれていない場合、警告が大量に出るだけでテストは緑。

- [ ] **Step 2: 既存テストファイルを Obsolete 抑制属性でラップ (推奨)**

`tests/rosettadds.Tests/Integration/*Tests.cs` のような、DomainParticipant を多用するファイル先頭に:

```csharp
#pragma warning disable CS0618 // Type or member is obsolete
```

を追加 (もしくは `#pragma warning restore` で閉じる)。一括置換が必要なら以下:

```bash
# 各テストファイル先頭に #pragma warning disable CS0618 を入れる
# ファイル末尾に #pragma warning restore CS0618 を入れる
```

これは **`dotnet test` の出力を見つつ、警告が目立つファイルに順次追加する** のが現実的。

一括で `rosettadds.Tests.csproj` に `<NoWarn>CS0618</NoWarn>` を追加するのが早いが、Unity 等への副作用を考えると、テストプロジェクト限定にとどめるのが安全。

**判断**: テストプロジェクトのみ `<NoWarn>CS0618</NoWarn>` を追加する。

`tests/rosettadds.Tests/rosettadds.Tests.csproj` に:

```xml
<PropertyGroup>
  <!-- DomainParticipant は Obsolete だが、既存テストの移行は別 PR のため警告を抑止する -->
  <NoWarn>$(NoWarn);CS0618</NoWarn>
</PropertyGroup>
```

を追加 (PropertyGroup がなければ新規、あれば既存に追記)。

- [ ] **Step 3: ビルド + テスト再確認**

```bash
dotnet build src/rosettadds/rosettadds.csproj
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj
```

期待: 既存 60+ テストがすべて緑。CS0618 警告なし。

- [ ] **Step 4: コミット**

```bash
git add tests/rosettadds.Tests/rosettadds.Tests.csproj
git commit -m "chore(test): DomainParticipant Obsolete 警告をテストプロジェクトで抑止"
```

### Task 5.4: samples / Unity / Perf Harness の確認

- [ ] **Step 1: samples ビルド**

```bash
dotnet build samples/TalkerListener/TalkerListener.csproj
dotnet build samples/SpdpDemo/SpdpDemo.csproj
```

期待: どちらも 0 エラー (Obsolete 警告は出るがビルドは緑)。

- [ ] **Step 2: 警告が問題にならないことの確認**

`dotnet build` の出力で `error` が 0 件であることを目視確認。`warning CS0618` だけが出力されるなら問題なし。

- [ ] **Step 3: Perf Harness / Unity ビルド (本リポ環境外なら skip)**

本リポには Ros2Unity は submodule / 別リポとして存在するため、CI 等で別途確認。ローカルで対象が無いなら、Unity ビルドはこのタスクでは skip して Phase 6 の手動確認に回す。

- [ ] **Step 4: コミット (変更があれば)**

samples / Unity / Perf Harness に変更があればコミット。なければ次へ。

---

## Phase 6: 全体検証

### Task 6.1: 全テスト実行

- [ ] **Step 1: 全テスト**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj
```

期待: 全件 PASS。失敗するテストがあれば原因を特定して修正する。

- [ ] **Step 2: Rcl 名前空間のテスト確認**

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~Rcl"
```

期待: 30+ 件 (Context 8 + ContextOptions 2 + Node 6 + NodeOptions 3 + DomainParticipantObsolete 4) 全件 PASS。

### Task 6.2: Unity .meta チェック

- [ ] **Step 1: check_unity_meta.sh 実行**

```bash
.github/scripts/check_unity_meta.sh
```

期待: クリーン (新規 .cs に対応する .meta がすべて存在し、orphan .meta がない)。

- [ ] **Step 2: 不足分の .meta 追加**

不足があれば、`.github/scripts/check_unity_meta.sh` のガイダンスに従い追加コミット。

### Task 6.3: ドキュメントコメント整備

- [ ] **Step 1: Context / ContextOptions / Node / NodeOptions の XML doc コメント確認**

`src/rosettadds/Rcl/*.cs` の public 型・メンバに `<summary>` が日本語で付いていることを確認。なければ追加する。

- [ ] **Step 2: コミット (変更があれば)**

```bash
git add src/rosettadds/Rcl/
git commit -m "docs(rcl): Rcl.Context / Rcl.Node の XML doc コメント整備"
```

### Task 6.4: 最終ビルド確認

- [ ] **Step 1: リポジトリ全体のビルド**

```bash
dotnet build rosettadds.sln
```

期待: 0 エラー。Obsolete 警告は出ても OK。

- [ ] **Step 2: 最終コミット (変更があれば)**

ビルドエラー修正等があれば追加コミット。

### Task 6.5: PR 準備

- [ ] **Step 1: ブランチ確認**

```bash
git branch --show-current
```

期待: `rcl/context-node-design`

- [ ] **Step 2: ログ確認**

```bash
git log --oneline main..HEAD
```

期待: 12-15 コミット程度。各コミットが小さく conventional commit 形式。

- [ ] **Step 3: PR 作成 (ユーザー指示があれば)**

PR 作成の指示があれば `gh pr create` を使う。それまでは push しない。

---

## 完了チェックリスト

- [ ] `Rcl.Context` / `Rcl.Node` / `Rcl.ContextOptions` / `Rcl.NodeOptions` の新規 unit test がすべて緑
- [ ] `Rcl.Node` 経由の `CreatePublisher` / `CreateSubscription` / `CreateServiceClient` が unit test で確認できる
- [ ] 既存 `tests/rosettadds.Tests/Integration/*` が緑のまま
- [ ] 既存 `UserEndpointManagerTests.cs` / `ParticipantTransportSetTests.cs` / `LeaseExpiryMonitorTests.cs` が緑のまま
- [ ] 既存 `samples/TalkerListener` / `samples/SpdpDemo` が変更なしでビルド可能
- [ ] 既存 `DomainParticipant` の挙動 (Pub/Sub 送受信、service client 送受信) が smoke test で確認できる
- [ ] `check_unity_meta.sh` がクリーン
- [ ] `ROSettaDDS.Rcl` 名前空間のドキュメントコメントが日本語で整備されている
- [ ] `DomainParticipant` の `Create*` メソッドに `[Obsolete]` 属性が付与されている
- [ ] `DomainParticipant` の `Create*` メソッドから `Rcl.Node` への委譲時に例外が発生しない
- [ ] PR を作成 (ユーザー指示があれば)
