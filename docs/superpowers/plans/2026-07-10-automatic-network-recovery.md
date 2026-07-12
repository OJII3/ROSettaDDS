# Wi-Fi 再接続時の自動ネットワーク復旧 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ネットワーク変更後も既存の `Context` / `Node` / publisher / subscriptionを維持し、組み込みUDP socketの再作成、multicast再join、SPDP/SEDP再広告によって通信を自動復旧する。

**Architecture:** `NetworkChange.NetworkAddressChanged` を内部通知源でラップし、デバウンスと最大3回の再試行を行う coordinator から `Context` の復旧操作を呼ぶ。既存コンポーネントの `IRtpsTransport` 参照を壊さないため、`UdpTransport` 自身が同一インスタンス内でsocketを再作成し、`ParticipantTransportSet` がowned transportと広告locatorを更新する。復旧後は各Nodeが保持するローカルendpointデータのlocatorを更新し、SEDP履歴を同一GUIDの最新状態へ差し替える。

**Tech Stack:** C# / .NET 8・netstandard2.1、xUnit、FluentAssertions、Unity 6 UPM package、System.Net.NetworkInformation

## Global Constraints

- 自動復旧は `ContextOptions.EnableAutomaticNetworkRecovery = true` を既定とする。
- `false` の場合はネットワーク変更通知を購読しない。
- デバウンスは1秒、最大試行回数は3回、再試行間隔は500ミリ秒とする。
- custom transportの `Start()` / `Stop()` / `Dispose()` を復旧処理から呼ばない。
- Android Java/Kotlin APIおよびUnityEngineへ依存しない。
- ネットワーク断中のuser sample永続キューは追加しない。
- `src/rosettadds` に追加するファイルとフォルダには `.meta` を追加する。
- コミットメッセージは日本語のConventional Commits形式とする。

---

## File Structure

- Create: `src/rosettadds/Transport/INetworkChangeSource.cs` — OSネットワーク変更通知の内部抽象。
- Create: `src/rosettadds/Transport/SystemNetworkChangeSource.cs` — `NetworkChange.NetworkAddressChanged` adapter。
- Create: `src/rosettadds/Rcl/NetworkRecoveryCoordinator.cs` — デバウンス、再試行、Dispose同期。
- Create: 上記3ファイルの `.meta`。
- Create: `tests/rosettadds.Tests/Rcl/NetworkRecoveryCoordinatorTests.cs` — 通知ライフサイクルの決定的テスト。
- Modify: `src/rosettadds/Rcl/ContextOptions.cs` — 自動復旧opt-out。
- Modify: `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs` — 既定値テスト。
- Modify: `src/rosettadds/Transport/UdpTransport.cs` — socket再作成とsend/restart排他。
- Modify: `tests/rosettadds.Tests/Transport/UdpTransportTests.cs` — 同一インスタンスの再起動テスト。
- Modify: `src/rosettadds/Dds/ParticipantTransportSet.cs` — owned UDP restartとlocator再列挙。
- Modify: `tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs` — custom除外、状態維持、locator更新。
- Modify: `src/rosettadds/Dds/EndpointSnapshot.cs` — 発見データsnapshot型。
- Modify: `src/rosettadds/Dds/EndpointRegistry.cs` — locatorをロック下で更新。
- Modify: `src/rosettadds/Dds/UserEndpointManager.cs` — registry操作の委譲。
- Modify: `src/rosettadds/Rcl/Node.cs` — Context向けlocator更新口。
- Modify: `tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs` — endpoint locator更新テスト。
- Modify: `src/rosettadds/Rcl/Context.cs` — coordinator所有と復旧フロー。
- Modify: `tests/rosettadds.Tests/Rcl/ContextTests.cs` — 購読解除、既存object維持、再広告テスト。

---

### Task 1: 自動復旧オプションとネットワーク変更通知源

**Files:**
- Create: `src/rosettadds/Transport/INetworkChangeSource.cs`
- Create: `src/rosettadds/Transport/INetworkChangeSource.cs.meta`
- Create: `src/rosettadds/Transport/SystemNetworkChangeSource.cs`
- Create: `src/rosettadds/Transport/SystemNetworkChangeSource.cs.meta`
- Modify: `src/rosettadds/Rcl/ContextOptions.cs`
- Test: `tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs`

**Interfaces:**
- Produces: `ContextOptions.EnableAutomaticNetworkRecovery : bool`
- Produces: `INetworkChangeSource.NetworkAddressChanged`
- Produces: `SystemNetworkChangeSource.Instance`

- [ ] **Step 1: 既定値の失敗テストを書く**

`ContextOptionsTests.すべてのプロパティに既定値が設定される` に次を追加する。

```csharp
Assert.True(opts.EnableAutomaticNetworkRecovery);
```

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ContextOptionsTests
```

Expected: `ContextOptions` にプロパティが存在せずコンパイル失敗。

- [ ] **Step 3: オプションと通知源を実装する**

`ContextOptions` に追加する。

```csharp
/// <summary>NICのIP変更時に組み込みUDP transportを自動復旧する。</summary>
public bool EnableAutomaticNetworkRecovery { get; init; } = true;
```

`INetworkChangeSource.cs`:

```csharp
using System.Net.NetworkInformation;

namespace ROSettaDDS.Transport;

internal interface INetworkChangeSource
{
    event NetworkAddressChangedEventHandler? NetworkAddressChanged;
}
```

`SystemNetworkChangeSource.cs`:

```csharp
using System.Net.NetworkInformation;

namespace ROSettaDDS.Transport;

internal sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    public static SystemNetworkChangeSource Instance { get; } = new();

    private SystemNetworkChangeSource() { }

    public event NetworkAddressChangedEventHandler? NetworkAddressChanged
    {
        add => NetworkChange.NetworkAddressChanged += value;
        remove => NetworkChange.NetworkAddressChanged -= value;
    }
}
```

`INetworkChangeSource.cs.meta`:

```yaml
fileFormatVersion: 2
guid: a3617f9e44b84a99984e3697b16334da
```

`SystemNetworkChangeSource.cs.meta`:

```yaml
fileFormatVersion: 2
guid: b825d1e785d249a6aa7d487da1652684
```

- [ ] **Step 4: GREENとmeta検査を確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ContextOptionsTests
.github/scripts/check_unity_meta.sh
```

Expected: 対象テストPASS、`All Unity meta files are valid.`。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Transport/INetworkChangeSource.cs src/rosettadds/Transport/INetworkChangeSource.cs.meta src/rosettadds/Transport/SystemNetworkChangeSource.cs src/rosettadds/Transport/SystemNetworkChangeSource.cs.meta src/rosettadds/Rcl/ContextOptions.cs tests/rosettadds.Tests/Rcl/ContextOptionsTests.cs
git commit -m "feat(network): 自動復旧オプションと変更通知源を追加"
```

---

### Task 2: 通知のデバウンスと再試行

**Files:**
- Create: `src/rosettadds/Rcl/NetworkRecoveryCoordinator.cs`
- Create: `src/rosettadds/Rcl/NetworkRecoveryCoordinator.cs.meta`
- Test: `tests/rosettadds.Tests/Rcl/NetworkRecoveryCoordinatorTests.cs`

**Interfaces:**
- Consumes: `INetworkChangeSource.NetworkAddressChanged`
- Produces: `NetworkRecoveryCoordinator(INetworkChangeSource, Func<CancellationToken, ValueTask>, ILogger, TimeSpan?, TimeSpan?, int?)`
- Produces: `NetworkRecoveryCoordinator.Dispose()`

- [ ] **Step 1: 連続通知・再試行・Disposeの失敗テストを書く**

fake sourceが `Raise()` とsubscriber countを持つようにし、次を独立したFactで検証する。

```csharp
[Fact]
public async Task 連続通知は最後の通知からデバウンスして1回復旧する()
{
    var source = new FakeNetworkChangeSource();
    var calls = 0;
    using var coordinator = new NetworkRecoveryCoordinator(
        source,
        _ => { Interlocked.Increment(ref calls); return default; },
        NullLogger.Instance,
        debounceDelay: TimeSpan.FromMilliseconds(30),
        retryDelay: TimeSpan.FromMilliseconds(1));

    source.Raise();
    source.Raise();
    source.Raise();
    await Task.Delay(100);

    calls.Should().Be(1);
}
```

`NetworkRecoveryCoordinator.cs.meta`:

```yaml
fileFormatVersion: 2
guid: c94082a059ec4ee08651fa583d281cb8
```

```csharp
[Fact]
public async Task 一時失敗は最大3回まで再試行する()
{
    var source = new FakeNetworkChangeSource();
    var calls = 0;
    using var coordinator = new NetworkRecoveryCoordinator(
        source,
        _ =>
        {
            Interlocked.Increment(ref calls);
            throw new SocketException((int)SocketError.NetworkDown);
        },
        NullLogger.Instance,
        debounceDelay: TimeSpan.FromMilliseconds(1),
        retryDelay: TimeSpan.FromMilliseconds(1),
        maxAttempts: 3);

    source.Raise();
    await Task.Delay(100);

    calls.Should().Be(3);
}
```

Disposeテストはsubscriber countが0になり、Dispose後の `Raise()` でoperationが呼ばれないことを確認する。

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~NetworkRecoveryCoordinatorTests
```

Expected: `NetworkRecoveryCoordinator` 未定義でコンパイル失敗。

- [ ] **Step 3: coordinatorを実装する**

実装定数は `1秒 / 500ミリ秒 / 3回`。通知ごとに以前のdebounce CTSをcancel/disposeし、operationは単一のworker task内で実行する。`Dispose()` はsourceからunsubscribeし、lifetime CTSをcancelし、workerを最大1秒待ってから全CTSをdisposeする。キャンセルは正常終了、その他の例外はattempt番号付きで `ILogger.Warn` へ記録する。

中心となる再試行ループは次とする。

```csharp
for (var attempt = 1; attempt <= _maxAttempts; attempt++)
{
    try
    {
        await _recover(_lifetimeCts.Token).ConfigureAwait(false);
        return;
    }
    catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
    {
        return;
    }
    catch (Exception ex)
    {
        _logger.Warn($"Network recovery attempt {attempt}/{_maxAttempts} failed", ex);
        if (attempt < _maxAttempts)
        {
            await Task.Delay(_retryDelay, _lifetimeCts.Token).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: GREENを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~NetworkRecoveryCoordinatorTests
.github/scripts/check_unity_meta.sh
```

Expected: 全対象テストPASS、meta検査PASS。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Rcl/NetworkRecoveryCoordinator.cs src/rosettadds/Rcl/NetworkRecoveryCoordinator.cs.meta tests/rosettadds.Tests/Rcl/NetworkRecoveryCoordinatorTests.cs
git commit -m "feat(network): 復旧通知をデバウンスして再試行する"
```

---

### Task 3: `UdpTransport` の同一インスタンスrestart

**Files:**
- Modify: `src/rosettadds/Transport/UdpTransport.cs`
- Test: `tests/rosettadds.Tests/Transport/UdpTransportTests.cs`

**Interfaces:**
- Produces: `internal void Restart()`
- Maintains: `LocalLocator`、`Received` event、start/stop状態

- [ ] **Step 1: unicastとmulticastの失敗テストを書く**

unicastは固定loopback portでreceiverを起動し、restart前後に同じ `Received` handlerが別々のpayloadを受け、`LocalLocator` と参照が不変であることを確認する。multicastは既存自己受信テストと同じgroup/interfaceでrestart後のpayloadを確認する。Dispose後は次を確認する。

```csharp
var transport = UdpTransport.CreateUnicast(IPAddress.Loopback, 0);
transport.Dispose();
Assert.Throws<ObjectDisposedException>(() => transport.Restart());
```

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~UdpTransportTests
```

Expected: `Restart()` 未定義でコンパイル失敗。

- [ ] **Step 3: socket構成保持とrestartを実装する**

`_socket` と `_localLocator` を可変にし、次の構成をreadonlyで保持する。

```csharp
private Socket _socket;
private Locator _localLocator;
private readonly IPAddress _bindAddress;
private readonly int _boundPort;
private readonly IPAddress? _joinInterface;
private readonly int _multicastTimeToLive;
private readonly SemaphoreSlim _sendGate = new(1, 1);
private readonly object _lifecycleLock = new();
```

unicast/multicast生成の重複を `CreateConfiguredSocket()` に集約し、初回にephemeral portが割り当てられた場合も `_boundPort = (int)localLocator.Port` としてrestart時に同一portをbindする。

`SendAsync` は `_sendGate.WaitAsync(cancellationToken)` から `finally { _sendGate.Release(); }` までsocket sendを囲む。`Restart()` はlifecycle lock内で開始状態を保存し、`StopCore()` 後に `_sendGate.Wait()` を取得して旧socketをdrop membership・dispose、新socketを生成して差し替え、必要なら `StartCore()` する。生成失敗時もgateを解放し、次のretryで再生成できる状態を保つ。`Dispose()` は同じlockとsend gateを使い、最後にgateをdisposeする。

- [ ] **Step 4: GREENと全Transportテストを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~Transport
```

Expected: restart追加テストを含むTransportテストPASS。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Transport/UdpTransport.cs tests/rosettadds.Tests/Transport/UdpTransportTests.cs
git commit -m "fix(transport): UDP socketを同一インスタンスで再作成する"
```

---

### Task 4: transport setのowned restartとlocator更新

**Files:**
- Modify: `src/rosettadds/Dds/ParticipantTransportSet.cs`
- Test: `tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs`

**Interfaces:**
- Consumes: `UdpTransport.Restart()`
- Produces: `ParticipantTransportSet.RestartOwnedTransports()`
- Updates: `MetatrafficUnicastLocators` / `DefaultUnicastLocators`

- [ ] **Step 1: 状態維持・custom除外・locator更新の失敗テストを書く**

`Create` に内部テスト用 `Func<IReadOnlyList<IPAddress>>? addressProvider` を追加する。providerが最初は `127.0.0.2`、restart時は `127.0.0.3` を返すテストで両locatorの更新を確認する。組み込みtransportの参照がrestart前後で同一であること、Start後は送受信を再開できることも確認する。全custom transport構成では `RestartOwnedTransports()` 後のrecording callsが増えないことを確認する。

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ParticipantTransportSetTests
```

Expected: 新しいoverloadと `RestartOwnedTransports()` がなくコンパイル失敗。

- [ ] **Step 3: owned transport restartとlocator再計算を実装する**

`ParticipantTransportSet` がoptionsとaddress providerを保持し、locator propertiesをprivate setへ変更する。

```csharp
public IReadOnlyList<Locator> MetatrafficUnicastLocators { get; private set; }
public IReadOnlyList<Locator> DefaultUnicastLocators { get; private set; }

internal void RestartOwnedTransports()
{
    ThrowIfDisposed();
    _metatrafficMulticast.RestartIfOwned();
    _metatrafficUnicast.RestartIfOwned();
    _userMulticast.RestartIfOwned();
    _userUnicast.RestartIfOwned();

    var addresses = ResolveAdvertisedAddresses(_options, _addressProvider);
    MetatrafficUnicastLocators = BuildUnicastLocators(
        _options.CustomUnicastTransport, _metatrafficUnicast, addresses);
    DefaultUnicastLocators = BuildUnicastLocators(
        _options.CustomUserUnicastTransport, _userUnicast, addresses);
}
```

`OwnedTransport.RestartIfOwned()` は `_ownsTransport && Transport is UdpTransport udp` の場合だけ `udp.Restart()` を呼ぶ。custom transportには何も行わない。

- [ ] **Step 4: GREENを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ParticipantTransportSetTests
```

Expected: 対象テストPASS。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Dds/ParticipantTransportSet.cs tests/rosettadds.Tests/Dds/ParticipantTransportSetTests.cs
git commit -m "fix(dds): transport再作成後にlocatorを更新する"
```

---

### Task 5: ローカルendpoint locatorの更新

**Files:**
- Modify: `src/rosettadds/Dds/EndpointSnapshot.cs`
- Modify: `src/rosettadds/Dds/EndpointRegistry.cs`
- Modify: `src/rosettadds/Dds/UserEndpointManager.cs`
- Modify: `src/rosettadds/Rcl/Node.cs`
- Test: `tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs`

**Interfaces:**
- Produces: `EndpointDiscoverySnapshot`
- Produces: `EndpointRegistry.UpdateLocalLocators(IReadOnlyList<Locator>, Locator)`
- Produces: `Node.RefreshLocalEndpointLocators(...)`

- [ ] **Step 1: writer/reader locator更新の失敗テストを書く**

registryへwriter/readerを登録し、新しいunicast 2件とmulticast 1件を渡す。返却snapshotと元のendpoint dataの双方で旧locatorが消え、新locatorだけになることを確認する。

```csharp
var snapshot = registry.UpdateLocalLocators(newUnicast, newMulticast);
snapshot.Writers.Should().ContainSingle();
snapshot.Readers.Should().ContainSingle();
snapshot.Writers[0].UnicastLocators.Should().Equal(newUnicast);
snapshot.Readers[0].MulticastLocators.Should().Equal(newMulticast);
```

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointRegistryTests
```

Expected: `UpdateLocalLocators` 未定義でコンパイル失敗。

- [ ] **Step 3: ロック下のlocator更新と委譲口を実装する**

`EndpointSnapshot.cs` に追加する。

```csharp
using ROSettaDDS.Discovery;

internal readonly record struct EndpointDiscoverySnapshot(
    DiscoveredEndpointData[] Writers,
    DiscoveredEndpointData[] Readers);
```

`EndpointRegistry.UpdateLocalLocators` は `_lock` 内で `_writers` と `_readers` の各要素についてlocator listを `Clear()` → `AddRange()` し、配列snapshotを返す。`UserEndpointManager` は同名メソッドを委譲し、`Node` は次を提供する。

```csharp
internal EndpointDiscoverySnapshot RefreshLocalEndpointLocators(
    IReadOnlyList<Locator> unicastLocators,
    Locator multicastLocator)
{
    if (_disposed)
    {
        return new EndpointDiscoverySnapshot(
            Array.Empty<DiscoveredEndpointData>(),
            Array.Empty<DiscoveredEndpointData>());
    }
    return _userEndpoints.UpdateLocalLocators(unicastLocators, multicastLocator);
}
```

- [ ] **Step 4: GREENを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointRegistryTests
```

Expected: 対象テストPASS。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Dds/EndpointSnapshot.cs src/rosettadds/Dds/EndpointRegistry.cs src/rosettadds/Dds/UserEndpointManager.cs src/rosettadds/Rcl/Node.cs tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs
git commit -m "feat(discovery): ローカルendpointのlocatorを更新可能にする"
```

---

### Task 6: `Context` の自動復旧とSPDP/SEDP再広告

**Files:**
- Modify: `src/rosettadds/Rcl/Context.cs`
- Test: `tests/rosettadds.Tests/Rcl/ContextTests.cs`

**Interfaces:**
- Consumes: `NetworkRecoveryCoordinator`
- Consumes: `ParticipantTransportSet.RestartOwnedTransports()`
- Consumes: `Node.RefreshLocalEndpointLocators(...)`
- Produces: `internal ValueTask RecoverNetworkAsync(CancellationToken)`

- [ ] **Step 1: 購読ライフサイクルと既存object維持の失敗テストを書く**

内部constructorへ `INetworkChangeSource` を渡せるようにする。自動復旧有効時はsubscriber countが1、`Context.Dispose()` 後は0、無効時は常に0を確認する。

loopback ContextをStartし、Node、publisher、subscriptionを作成して、`RecoverNetworkAsync` 前後で `UserMulticastTransport` が同一参照であることを確認する。復旧後もpublisherの `PublishAsync` が成功し、Nodeから追加publisherを作成できることを確認する。

- [ ] **Step 2: REDを確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ContextTests
```

Expected: 内部constructorと `RecoverNetworkAsync` がなくコンパイル失敗。

- [ ] **Step 3: coordinator所有と復旧フローを実装する**

公開constructorはsystem sourceを使う。

```csharp
public Context(ContextOptions options)
    : this(options, SystemNetworkChangeSource.Instance)
{
}
```

既存初期化を内部constructorへ移し、optionsが有効な場合だけcoordinatorを作る。

```csharp
if (_options.EnableAutomaticNetworkRecovery)
{
    _networkRecovery = new NetworkRecoveryCoordinator(
        networkChangeSource,
        RecoverNetworkAsync,
        _options.Logger);
}
```

`RecoverNetworkAsync` はContext単位の `SemaphoreSlim` で直列化し、次の順を厳守する。

```csharp
internal async ValueTask RecoverNetworkAsync(CancellationToken cancellationToken)
{
    await _networkRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    var restartDiscoveryWriters = false;
    try
    {
        if (_disposed) return;
        restartDiscoveryWriters = _started;
        if (restartDiscoveryWriters)
        {
            _spdpWriter.Stop();
            _sedpPublicationsWriter.Stop();
            _sedpSubscriptionsWriter.Stop();
        }

        _transports.RestartOwnedTransports();
        var nodes = SnapshotNodes();
        foreach (var node in nodes)
        {
            var endpoints = node.RefreshLocalEndpointLocators(
                _transports.DefaultUnicastLocators,
                _transports.UserMulticastDestination);
            foreach (var writer in endpoints.Writers)
                await _sedpPublicationsWriter.AddEndpointAsync(writer, cancellationToken).ConfigureAwait(false);
            foreach (var reader in endpoints.Readers)
                await _sedpSubscriptionsWriter.AddEndpointAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }
    finally
    {
        if (restartDiscoveryWriters && !_disposed)
        {
            _sedpPublicationsWriter.Start();
            _sedpSubscriptionsWriter.Start();
            _spdpWriter.Start();
        }
        _networkRecoveryGate.Release();
    }
}
```

`SnapshotNodes()` は `_nodesLock` 下で配列化する。`Dispose()` は最初にcoordinatorをdisposeして保留復旧を止め、その後既存の `Stop()` とNode/transport破棄を行い、最後にrecovery gateをdisposeする。失敗経路でも `wasStarted` ならdiscovery writerを再開する処理を `finally` へ置き、coordinatorの次attemptが実行可能な状態を保つ。

- [ ] **Step 4: GREENとRcl/Dds回帰を確認する**

Run:

```bash
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ContextTests|FullyQualifiedName~NodeTests|FullyQualifiedName~ParticipantTransportSetTests|FullyQualifiedName~EndpointRegistryTests"
```

Expected: 対象テストPASS。

- [ ] **Step 5: コミットする**

```bash
git add src/rosettadds/Rcl/Context.cs tests/rosettadds.Tests/Rcl/ContextTests.cs
git commit -m "fix(rcl): ネットワーク変更後にDDS通信を自動復旧する"
```

---

### Task 7: 全体検証とPR準備

**Files:**
- Verify: repository全体。検証で本変更由来の不具合を検出した場合は、該当Taskへ戻って失敗テストを追加してから修正する。

**Interfaces:**
- Produces: 検証結果とPR本文。

- [ ] **Step 1: formatterと差分検査を実行する**

Run:

```bash
dotnet format rosettadds.sln --verify-no-changes
git diff --check origin/main...HEAD
```

Expected: いずれもexit 0。format差分が必要なら `dotnet format rosettadds.sln` を実行し、関連テストを再実行して別コミットにする。

- [ ] **Step 2: 全.NETテストを実行する**

Run:

```bash
dotnet test rosettadds.sln
```

Expected: 全テストPASS、失敗0。

- [ ] **Step 3: Unity metaを検査する**

Run:

```bash
.github/scripts/check_unity_meta.sh
```

Expected: `All Unity meta files are valid.`。

- [ ] **Step 4: Unity Editor検証の可否を確認する**

UPM package cacheが復旧していれば既存スクリプトでEditModeを実行する。

```bash
scripts/unity/run_editmode.sh
```

Expected: PASS。UPM/DNSによる既知のpackage欠落で失敗する場合はログから同原因であることを確認し、PRのTestingへ「環境要因で未実施」と明記する。ソース由来の新規コンパイルエラーは修正する。

- [ ] **Step 5: statusとコミット履歴を確認する**

Run:

```bash
git status --short --branch
git log --oneline origin/main..HEAD
```

Expected: worktree clean、設計・計画・段階実装コミットが日本語Conventional Commitsで並ぶ。

- [ ] **Step 6: pushしてPull Requestを作成する**

```bash
git push -u origin codex/fix-automatic-network-recovery
gh pr create --base main --head codex/fix-automatic-network-recovery --title "fix: Wi-Fi再接続後にDDS通信を自動復旧する" --body-file /tmp/rosettadds-network-recovery-pr.md
```

PR本文には原因、同一transportインスタンスでのsocket再作成、locator更新、SPDP/SEDP再広告、custom transport除外、各検証結果、Quest実機検証が別途必要なことを記載する。
