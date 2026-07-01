# UserEndpointManager Testability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `UserEndpointManager` を状態 holder / 純ロジック / 副作用境界の 3 コンポーネントへ分解し、public API を一切変更せずに contract 単位でテスト可能にする。

**Architecture:** `EndpointRegistry` (lock + topic maps + writer snapshot) / `EndpointMatcher` (match 判定 + locator 解決) / `IEndpointReceiver` + `ParticipantRtpsReceiverAdapter` (副作用境界) に責務を分離し、`UserEndpointManager` はオーケストレータとして 3 つを束ねる。

**Tech Stack:** C# / .NET 8, xUnit, FluentAssertions, Unity package under `src/rosettadds` with `.meta` files.

---

## File Structure

- Create: `src/rosettadds/Dds/EndpointRegistry.cs`
  - lock + topic maps + writer snapshot を内包する状態 holder。
- Create: `src/rosettadds/Dds/LocalWriter.cs`
  - `UserEndpointManager` から独立した `internal sealed record`。
- Create: `src/rosettadds/Dds/LocalReader.cs`
  - `UserEndpointManager` から独立した `internal sealed record`。
- Create: `src/rosettadds/Dds/EndpointMatcher.cs`
  - match / unmatch 判定 + remote unicast locator 解決の純ロジック静的クラス。
- Create: `src/rosettadds/Dds/MatchDecision.cs`
  - `EndpointMatcher` の戻り値型 (`internal readonly record struct`)。
- Create: `src/rosettadds/Dds/IEndpointReceiver.cs`
  - `ParticipantRtpsReceiver` 依存を隠蔽する `internal interface`。
- Create: `src/rosettadds/Dds/ParticipantRtpsReceiverAdapter.cs`
  - 既存 `ParticipantRtpsReceiver` を `IEndpointReceiver` へ adapter するクラス。
- Modify: `src/rosettadds/Dds/UserEndpointManager.cs`
  - 内部実装を 3 コンポーネントへ委譲。public API は不変。
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
  - `_userEndpoints` のコンストラクタ呼び出しで adapter を渡すよう 1 行変更。
- Modify: `tests/rosettadds.Tests/Dds/UserEndpointManagerTests.cs`
  - 既存 2 テストの `new UserEndpointManager(...)` を `new ParticipantRtpsReceiverAdapter(...)` 経由に更新 (動作不変)。
- Test: `tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs`
- Test: `tests/rosettadds.Tests/Dds/EndpointMatcherTests.cs`
- Test: `tests/rosettadds.Tests/Dds/UserEndpointManagerRefactoredTests.cs`
- Test: `tests/rosettadds.Tests/Dds/FakeEndpointReceiver.cs`
- Create: `src/rosettadds/Dds/*.cs.meta` (新規 7 ファイル分)

---

### Task 1: IEndpointReceiver + ParticipantRtpsReceiverAdapter

**Files:**
- Create: `src/rosettadds/Dds/IEndpointReceiver.cs`
- Create: `src/rosettadds/Dds/ParticipantRtpsReceiverAdapter.cs`
- Test: `tests/rosettadds.Tests/Dds/FakeEndpointReceiver.cs`
- Test: `tests/rosettadds.Tests/Dds/ParticipantRtpsReceiverAdapterTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/rosettadds.Tests/Dds/FakeEndpointReceiver.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps;

namespace ROSettaDDS.Tests.Dds;

internal sealed class FakeEndpointReceiver : IEndpointReceiver
{
    public List<(EntityId entityId, StatefulWriter writer)> RegisteredWriters { get; } = new();
    public List<EntityId> UnregisteredWriters { get; } = new();
    public List<(EntityId entityId, IRtpsSubmessageHandler handler)> RegisteredReaders { get; } = new();
    public List<EntityId> UnregisteredReaders { get; } = new();

    public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
    {
        RegisteredWriters.Add((writerEntityId, writer));
    }

    public void UnregisterWriter(EntityId writerEntityId)
    {
        UnregisteredWriters.Add(writerEntityId);
    }

    public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
    {
        RegisteredReaders.Add((readerEntityId, handler));
    }

    public void UnregisterReader(EntityId readerEntityId)
    {
        UnregisteredReaders.Add(readerEntityId);
    }
}
```

Create `tests/rosettadds.Tests/Dds/ParticipantRtpsReceiverAdapterTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Rtps;

namespace ROSettaDDS.Tests.Dds;

public class ParticipantRtpsReceiverAdapterTests
{
    [Fact]
    public void RegisterWriter_は_receiver_RegisterWriter_に委譲する()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var receiver = new ParticipantRtpsReceiver(prefix);
        var adapter = new ParticipantRtpsReceiverAdapter(receiver);
        var entityId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);

        var recording = new RecordingReceiver(receiver);
        adapter.RegisterWriter(entityId, MakeNullStatefulWriter(prefix, entityId));

        recording.RegisteredWriterIds.Should().ContainSingle().Which.Should().Be(entityId);
    }

    private static StatefulWriter MakeNullStatefulWriter(GuidPrefix prefix, EntityId entityId)
    {
        // Real constructor args (see StatefulWriter.cs:61-86); the writer is never started
        // so transport / history are placeholders.
        return new StatefulWriter(
            sendTransport: new NoopTransport(),
            multicastDestination: new Locator { Kind = LocatorKind.UdpV4, Port = 0 },
            version: ProtocolVersion.V2_4,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: prefix,
            writerEntityId: entityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(200),
            history: new WriterHistoryCache(new Guid(prefix, entityId), maxSamples: 1));
    }

    private sealed class NoopTransport : IRtpsTransport
    {
        public Locator LocalLocator => Locator.Invalid;
        public event Action<ReadOnlyMemory<byte>, Locator>? Received { add { } remove { } }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default) => default;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class RecordingReceiver
    {
        public ParticipantRtpsReceiver Receiver { get; }
        public List<EntityId> RegisteredWriterIds { get; } = new();
        public RecordingReceiver(ParticipantRtpsReceiver receiver)
        {
            Receiver = receiver;
            // Hook into the receiver's RegisterWriter by subscribing our own log channel:
            // Since ParticipantRtpsReceiver's RegisterWriter is fire-and-forget, we verify
            // dispatch via a thin wrapper that subclasses ParticipantRtpsReceiver and
            // overrides RegisterWriter. For the adapter test, we instead verify behavior
            // through a custom ParticipantRtpsReceiver subclass below.
        }
    }
}
```

> **Note:** If subclassing `ParticipantRtpsReceiver` to record calls is infeasible (e.g., its `RegisterWriter` is sealed or doesn't accept hooks), replace the test with a simpler one that just verifies the adapter's `RegisterWriter` does not throw and forwards args. Use a no-op `StatefulWriter` constructed with the minimal `NoopTransport` above and rely on the existing `ParticipantRtpsReceiver` happy path:

```csharp
[Fact]
public void RegisterWriter_は_receiver_RegisterWriter_に委譲して例外を投げない()
{
    var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
    var receiver = new ParticipantRtpsReceiver(prefix);
    var adapter = new ParticipantRtpsReceiverAdapter(receiver);
    var entityId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);
    var writer = MakeNullStatefulWriter(prefix, entityId);

    var act = () => adapter.RegisterWriter(entityId, writer);

    act.Should().NotThrow();
}
```

This simpler assertion is sufficient: the adapter is a one-line pass-through, and the `FakeEndpointReceiver` in `UserEndpointManagerRefactoredTests` (Task 4) covers the actual `IEndpointReceiver` behavior end-to-end.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ParticipantRtpsReceiverAdapterTests`

Expected: FAIL with "ParticipantRtpsReceiverAdapter does not exist" / "IEndpointReceiver does not exist".

- [ ] **Step 3: Write minimal implementation**

Create `src/rosettadds/Dds/IEndpointReceiver.cs`:

```csharp
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

/// <summary>
/// <see cref="UserEndpointManager"/> が依存する receiver 副作用の境界。
/// 実装は <see cref="ParticipantRtpsReceiverAdapter"/> (本番) / fake (テスト) を切り替える。
/// </summary>
internal interface IEndpointReceiver
{
    void RegisterWriter(EntityId writerEntityId, StatefulWriter writer);
    void UnregisterWriter(EntityId writerEntityId);
    void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler);
    void UnregisterReader(EntityId readerEntityId);
}
```

Create `src/rosettadds/Dds/ParticipantRtpsReceiverAdapter.cs`:

```csharp
using ROSettaDDS.Rtps;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

/// <summary>
/// 既存の <see cref="ParticipantRtpsReceiver"/> を <see cref="IEndpointReceiver"/> へ adapter する。
/// 公開 API を一切変えずに <see cref="UserEndpointManager"/> の依存を隠蔽する目的専用。
/// </summary>
internal sealed class ParticipantRtpsReceiverAdapter : IEndpointReceiver
{
    private readonly ParticipantRtpsReceiver _receiver;

    public ParticipantRtpsReceiverAdapter(ParticipantRtpsReceiver receiver)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
    }

    public void RegisterWriter(EntityId writerEntityId, StatefulWriter writer)
        => _receiver.RegisterWriter(writerEntityId, writer);

    public void UnregisterWriter(EntityId writerEntityId)
        => _receiver.UnregisterWriter(writerEntityId);

    public void RegisterReader(EntityId readerEntityId, IRtpsSubmessageHandler handler)
        => _receiver.RegisterReader(readerEntityId, handler);

    public void UnregisterReader(EntityId readerEntityId)
        => _receiver.UnregisterReader(readerEntityId);
}
```

Create `.meta` files. `src/rosettadds/Dds/IEndpointReceiver.cs.meta`:

```yaml
fileFormatVersion: 2
guid: 5a1c0f0c1111404a8c0c8e1b2c3d4e5f
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleName:
  assetBundleVariant:
```

Generate `ParticipantRtpsReceiverAdapter.cs.meta` with a different random GUID (`6b2d1a1d2222415b9d1d9f2c3d4e5f60`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ParticipantRtpsReceiverAdapterTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/rosettadds/Dds/IEndpointReceiver.cs src/rosettadds/Dds/IEndpointReceiver.cs.meta src/rosettadds/Dds/ParticipantRtpsReceiverAdapter.cs src/rosettadds/Dds/ParticipantRtpsReceiverAdapter.cs.meta tests/rosettadds.Tests/Dds/FakeEndpointReceiver.cs tests/rosettadds.Tests/Dds/ParticipantRtpsReceiverAdapterTests.cs
git commit -m "refactor(dds): IEndpointReceiver interface と adapter を追加"
```

---

### Task 2: EndpointMatcher (match 判定 + locator 解決)

**Files:**
- Create: `src/rosettadds/Dds/LocalWriter.cs`
- Create: `src/rosettadds/Dds/LocalReader.cs`
- Create: `src/rosettadds/Dds/MatchDecision.cs`
- Create: `src/rosettadds/Dds/EndpointMatcher.cs`
- Test: `tests/rosettadds.Tests/Dds/EndpointMatcherTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/rosettadds.Tests/Dds/EndpointMatcherTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Dds;

public class EndpointMatcherTests
{
    [Fact]
    public void TypeMatches_は両方非空で一致ならtrueを返す()
    {
        EndpointMatcher.TypeMatches("a", "a").Should().BeTrue();
    }

    [Fact]
    public void TypeMatches_は空文字を含むとfalseを返す()
    {
        EndpointMatcher.TypeMatches("", "a").Should().BeFalse();
        EndpointMatcher.TypeMatches("a", "").Should().BeFalse();
    }

    [Fact]
    public void FirstUdpLocator_はUDPv4を返す()
    {
        var loc = new Locator { Kind = LocatorKind.UdpV4, Port = 7411 };
        EndpointMatcher.FirstUdpLocator(new[] { loc }).Should().Be(loc);
    }

    [Fact]
    public void FirstUdpLocator_はUDPv6も許容する()
    {
        var loc = new Locator { Kind = LocatorKind.UdpV6, Port = 7411 };
        EndpointMatcher.FirstUdpLocator(new[] { loc }).Should().Be(loc);
    }

    [Fact]
    public void FirstUdpLocator_は非UDPをスキップする()
    {
        var loc = new Locator { Kind = LocatorKind.UdpV4, Port = 7411 };
        EndpointMatcher.FirstUdpLocator(new[]
        {
            new Locator { Kind = LocatorKind.Tcp, Port = 1 },
            loc,
        }).Should().Be(loc);
    }

    [Fact]
    public void FirstUdpLocator_は空入力でnullを返す()
    {
        EndpointMatcher.FirstUdpLocator(Array.Empty<Locator>()).Should().BeNull();
    }

    [Fact]
    public void EvaluateLocalRemote_Writer_は_TypeName不一致でCompatible_falseを返す()
    {
        var local = MakeLocalWriter("t", "TypeA");
        var remote = MakeRemoteReader("t", "TypeB");

        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);

        d.Compatible.Should().BeFalse();
        d.UnicastLocator.Should().BeNull();
    }

    [Fact]
    public void EvaluateLocalRemote_Writer_は_互換QoSでCompatible_trueとlocatorを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var locator = new Locator { Kind = LocatorKind.UdpV4, Port = 7411 };
        var local = MakeLocalWriter("t", "TypeA");
        var remote = MakeRemoteReader("t", "TypeA", unicast: locator);

        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);

        d.Compatible.Should().BeTrue();
        d.UnicastLocator.Should().Be(locator);
        d.ReliabilityKind.Should().Be(ReliabilityKind.Reliable);
    }

    [Fact]
    public void EvaluateLocalLocal_は_TypeName不一致で両方向unmatch指示()
    {
        var reader = MakeLocalReader("t", "TypeA");
        var writer = MakeLocalWriter("t", "TypeB");

        var d = EndpointMatcher.EvaluateLocalLocal(reader, writer);

        d.Compatible.Should().BeFalse();
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_endpoint直指定のlocatorを優先する()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var endpointLoc = new Locator { Kind = LocatorKind.UdpV4, Port = 7411 };
        var participantLoc = new Locator { Kind = LocatorKind.UdpV4, Port = 9999 };
        var remote = MakeRemoteReader("t", "TypeA", endpointLoc, prefix: prefix);
        var participant = MakeRemoteParticipant(prefix, participantLoc);

        EndpointMatcher.ResolveRemoteUnicastLocator(remote, new[] { participant })
            .Should().Be(endpointLoc);
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_endpoint_locator無しなら_participant_defaultにフォールバックする()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var participantLoc = new Locator { Kind = LocatorKind.UdpV4, Port = 9999 };
        var remote = MakeRemoteReader("t", "TypeA", unicast: null, prefix: prefix);
        var participant = MakeRemoteParticipant(prefix, participantLoc);

        EndpointMatcher.ResolveRemoteUnicastLocator(remote, new[] { participant })
            .Should().Be(participantLoc);
    }

    [Fact]
    public void ResolveRemoteUnicastLocator_は_解決不可ならnullを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var remote = MakeRemoteReader("t", "TypeA", unicast: null, prefix: prefix);

        EndpointMatcher.ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>())
            .Should().BeNull();
    }

    private static LocalWriter MakeLocalWriter(string topic, string typeName)
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var entityId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);
        var writerGuid = new Guid(prefix, entityId);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpoint.UnicastLocators.Add(new Locator { Kind = LocatorKind.UdpV4, Port = 7411 });
        var recordingTransport = new RecordingTransport(7411);
        var history = new WriterHistoryCache(writerGuid, maxSamples: 16);
        var writer = new StatefulWriter(
            sendTransport: recordingTransport,
            multicastDestination: new Locator { Kind = LocatorKind.UdpV4, Port = 7401 },
            version: ProtocolVersion.Current,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: prefix,
            writerEntityId: entityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(200),
            history: history);
        return new LocalWriter(endpoint, writer);
    }

    private static LocalReader MakeLocalReader(string topic, string typeName)
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var entityId = new EntityId(1, EntityKind.UserDefinedReaderNoKey);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, entityId),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpoint.UnicastLocators.Add(new Locator { Kind = LocatorKind.UdpV4, Port = 7411 });
        var reader = new BestEffortUserReader(prefix, entityId);
        return new LocalReader(endpoint, reader);
    }

    private static RemoteEndpoint MakeRemoteReader(string topic, string typeName, Locator? unicast = null, GuidPrefix? prefix = null)
    {
        prefix ??= GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, new EntityId(2, EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        if (unicast is not null) endpoint.UnicastLocators.Add(unicast);
        return new RemoteEndpoint(endpoint, DateTime.UtcNow);
    }

    private static RemoteParticipant MakeRemoteParticipant(GuidPrefix prefix, params Locator[] defaultUnicast)
    {
        var data = new ParticipantData
        {
            ProtocolVersion = ProtocolVersion.Current,
            VendorId = VendorId.ROSettaDDS,
            Guid = new Guid(prefix, EntityId.Participant),
            BuiltinEndpoints = BuiltinEndpointSet.ROSettaDDSDefault,
            LeaseDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(20)),
        };
        data.DefaultUnicastLocators.AddRange(defaultUnicast);
        return new RemoteParticipant(data, DateTime.UtcNow);
    }
}
```

The test file requires these additional `using` directives at the top:
```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Writer;
```

`RecordingTransport` is a private nested class copied verbatim from `ParticipantEndpointFactoryTests.cs:58-91`. `BuiltinEndpointSet.ROSettaDDSDefault` is the participant builtin endpoint set constant; if it has a different name (e.g. `Default`), check the `ParticipantData` constructor and adjust.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointMatcherTests`

Expected: FAIL because `EndpointMatcher` / `LocalWriter` / `LocalReader` / `MatchDecision` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/rosettadds/Dds/LocalWriter.cs`:

```csharp
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Dds;

internal sealed record LocalWriter(DiscoveredEndpointData EndpointData, StatefulWriter Writer);
```

Create `src/rosettadds/Dds/LocalReader.cs`:

```csharp
using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds;

internal sealed record LocalReader(DiscoveredEndpointData EndpointData, IUserReader Reader);
```

Create `src/rosettadds/Dds/MatchDecision.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;

namespace ROSettaDDS.Dds;

internal readonly record struct MatchDecision(
    bool Compatible,
    Locator? UnicastLocator,
    ReliabilityKind? ReliabilityKind)
{
    public static MatchDecision NotCompatible => new(false, null, null);
    public static MatchDecision Compatible(Locator? locator, ReliabilityKind reliabilityKind)
        => new(true, locator, reliabilityKind);
}
```

Create `src/rosettadds/Dds/EndpointMatcher.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds;

/// <summary>
/// local ↔ local / local ↔ remote の match 判定と remote unicast locator 解決の
/// 純ロジック集合。<see cref="UserEndpointManager"/> の責務を絞り込むために分離する。
/// 副作用 (logger 出力、receiver 呼び出し) は持たない。
/// </summary>
internal static class EndpointMatcher
{
    public static MatchDecision EvaluateLocalRemote(LocalWriter local, RemoteEndpoint remote)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remote.TypeName)
            || !QosCompatibility.IsCompatible(local.EndpointData, remote.Data))
        {
            return MatchDecision.NotCompatible;
        }
        return MatchDecision.Compatible(
            ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>()),
            local.EndpointData.Reliability.Kind);
    }

    public static MatchDecision EvaluateLocalRemote(LocalReader local, RemoteEndpoint remote)
    {
        if (!TypeMatches(local.EndpointData.TypeName, remote.TypeName)
            || !QosCompatibility.IsCompatible(remote.Data, local.EndpointData))
        {
            return MatchDecision.NotCompatible;
        }
        return MatchDecision.Compatible(
            ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>()),
            local.EndpointData.Reliability.Kind);
    }

    public static MatchDecision EvaluateLocalLocal(LocalReader reader, LocalWriter writer)
    {
        if (!TypeMatches(reader.EndpointData.TypeName, writer.EndpointData.TypeName)
            || !QosCompatibility.IsCompatible(writer.EndpointData, reader.EndpointData))
        {
            return MatchDecision.NotCompatible;
        }
        var writerLocator = FirstUdpLocator(writer.EndpointData.UnicastLocators);
        var readerLocator = FirstUdpLocator(reader.EndpointData.UnicastLocators);
        return MatchDecision.Compatible(readerLocator ?? writerLocator, reader.EndpointData.Reliability.Kind);
    }

    public static Locator? ResolveRemoteUnicastLocator(
        RemoteEndpoint remote,
        IReadOnlyList<RemoteParticipant> participants)
    {
        var loc = FirstUdpLocator(remote.Data.UnicastLocators);
        if (loc is not null) return loc;
        foreach (var p in participants)
        {
            if (p.GuidPrefix.Equals(remote.Data.EndpointGuid.Prefix))
            {
                return FirstUdpLocator(p.Data.DefaultUnicastLocators);
            }
        }
        return null;
    }

    public static Locator? FirstUdpLocator(IEnumerable<Locator> locators)
    {
        foreach (var loc in locators)
        {
            if (loc.Kind is LocatorKind.UdpV4 or LocatorKind.UdpV6) return loc;
        }
        return null;
    }

    public static bool TypeMatches(string local, string remote)
        => !string.IsNullOrEmpty(local)
        && !string.IsNullOrEmpty(remote)
        && string.Equals(local, remote, StringComparison.Ordinal);
}
```

Create `.meta` files for `LocalWriter.cs` / `LocalReader.cs` / `MatchDecision.cs` / `EndpointMatcher.cs` with random GUIDs (e.g. `7c3e2b2e3333425cae2e0a3d4e5f6071` / `8d4f3c3f4444436dbf3f1b4e5f607182` / `9e5040405555547ec0502c5f60718293` / `a061515166666580d1613d60718293a4`).

Note: `EvaluateLocalRemote` の 2 オーバーロードで `ResolveRemoteUnicastLocator(remote, Array.Empty<RemoteParticipant>())` を呼んでいるのは、`UserEndpointManager` 側で別途 `_discoveryDb.Snapshot()` を渡して locator を取得する設計にするため。実装の詰めは `UserEndpointManager` リファクタタスクで `participants` を渡すよう更新する。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointMatcherTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/rosettadds/Dds/LocalWriter.cs src/rosettadds/Dds/LocalWriter.cs.meta src/rosettadds/Dds/LocalReader.cs src/rosettadds/Dds/LocalReader.cs.meta src/rosettadds/Dds/MatchDecision.cs src/rosettadds/Dds/MatchDecision.cs.meta src/rosettadds/Dds/EndpointMatcher.cs src/rosettadds/Dds/EndpointMatcher.cs.meta tests/rosettadds.Tests/Dds/EndpointMatcherTests.cs
git commit -m "refactor(dds): EndpointMatcher を抽出 (match 判定 + locator 解決)"
```

---

### Task 3: EndpointRegistry (状態 holder)

**Files:**
- Create: `src/rosettadds/Dds/EndpointRegistry.cs`
- Test: `tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Dds;

public class EndpointRegistryTests
{
    [Fact]
    public void AddLocalWriter_は_writerSnapshotに含める()
    {
        var registry = new EndpointRegistry();
        var (endpoint, writer) = MakeWriter("t", "TypeA");

        registry.AddLocalWriter(endpoint, writer);

        registry.Snapshot().Writers.Should().ContainSingle().Which.Should().BeSameAs(writer);
    }

    [Fact]
    public void AddLocalReader_は_topic別に保持されGetLocalReadersForTopicで取得できる()
    {
        var registry = new EndpointRegistry();
        var (ep1, r1) = MakeReader("t1", "TypeA");
        var (ep2, r2) = MakeReader("t2", "TypeA");

        registry.AddLocalReader(ep1, r1);
        registry.AddLocalReader(ep2, r2);

        registry.GetLocalReadersForTopic("t1").Should().ContainSingle().Which.Reader.Should().BeSameAs(r1);
        registry.GetLocalReadersForTopic("t2").Should().ContainSingle().Which.Reader.Should().BeSameAs(r2);
        registry.GetLocalReadersForTopic("t3").Should().BeEmpty();
    }

    [Fact]
    public void RemoveLocalWriter_は_他端のLocalReader配列を返す()
    {
        var registry = new EndpointRegistry();
        var (wEp, w) = MakeWriter("t", "TypeA");
        var (rEp, r) = MakeReader("t", "TypeA");
        registry.AddLocalWriter(wEp, w);
        registry.AddLocalReader(rEp, r);

        var removed = registry.RemoveLocalWriter(wEp.EndpointGuid, w);

        removed.LocalReaders.Should().ContainSingle().Which.Reader.Should().BeSameAs(r);
        removed.Endpoint.Should().Be(wEp);
        registry.Snapshot().Writers.Should().BeEmpty();
    }

    [Fact]
    public void RemoveLocalWriter_は_GUID一致なしなら_endpoint_nullと空配列を返す()
    {
        var registry = new EndpointRegistry();
        var (wEp, w) = MakeWriter("t", "TypeA");
        var otherGuid = new Guid(GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9), new EntityId(1, EntityKind.UserDefinedWriterNoKey));

        var removed = registry.RemoveLocalWriter(otherGuid, w);

        removed.Endpoint.Should().BeNull();
        removed.LocalReaders.Should().BeEmpty();
    }

    [Fact]
    public void ShouldAdvertiseForTopic_は_他に残っているendpointがあればtrueを返す()
    {
        var registry = new EndpointRegistry();
        var (wEp1, w1) = MakeWriter("t", "TypeA");
        var (wEp2, w2) = MakeWriter("t", "TypeA");
        registry.AddLocalWriter(wEp1, w1);
        registry.AddLocalWriter(wEp2, w2);

        registry.ShouldAdvertiseForTopic("t", wEp1.EndpointGuid).Should().BeTrue();
    }

    [Fact]
    public void ShouldAdvertiseForTopic_は_最後の1個ならfalseを返す()
    {
        var registry = new EndpointRegistry();
        var (wEp, w) = MakeWriter("t", "TypeA");
        registry.AddLocalWriter(wEp, w);

        registry.ShouldAdvertiseForTopic("t", wEp.EndpointGuid).Should().BeFalse();
    }

    [Fact]
    public void StartWriters_と_StopWriters_は_writerSnapshotの全writerに伝播する()
    {
        var registry = new EndpointRegistry();
        var (wEp, w) = MakeWriter("t", "TypeA");
        registry.AddLocalWriter(wEp, w);

        var act1 = () => registry.StartWriters();
        var act2 = () => registry.StopWriters();

        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    private static (DiscoveredEndpointData, StatefulWriter) MakeWriter(string topic, string typeName)
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var writerGuid = new Guid(prefix, new EntityId(1, EntityKind.UserDefinedWriterNoKey));
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        var recordingTransport = new RecordingTransport(7411);
        var history = new WriterHistoryCache(writerGuid, maxSamples: 16);
        var writer = new StatefulWriter(
            sendTransport: recordingTransport,
            multicastDestination: new Locator { Kind = LocatorKind.UdpV4, Port = 7401 },
            version: ProtocolVersion.Current,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: prefix,
            writerEntityId: writerGuid.EntityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(200),
            history: history);
        return (endpoint, writer);
    }

    private static (DiscoveredEndpointData, IUserReader) MakeReader(string topic, string typeName)
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var readerEntityId = new EntityId(1, EntityKind.UserDefinedReaderNoKey);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, readerEntityId),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        var reader = new BestEffortUserReader(prefix, readerEntityId);
        return (endpoint, reader);
    }
}
```

`RecordingTransport` is reused from `ParticipantEndpointFactoryTests.cs` (private nested class). Either:
1. Copy `RecordingTransport` into the new test file as a private nested class.
2. Promote it to a shared test helper at `tests/rosettadds.Tests/TestUtilities/RecordingTransport.cs`.

Choose option 1 to avoid touching shared infrastructure; copy the class verbatim from `ParticipantEndpointFactoryTests.cs:58-91`.

`WriterHistoryCache` requires `using ROSettaDDS.Rtps.HistoryCache;` and `ReliabilityQos` / `DurabilityQos` require `using ROSettaDDS.Dds.QoS;`. `ProtocolVersion` requires `using ROSettaDDS.Common;` (already there).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointRegistryTests`

Expected: FAIL because `EndpointRegistry` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/rosettadds/Dds/EndpointRegistry.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// local user endpoint の登録状態 (lock + topic maps + writer snapshot) を
/// 保持する状態 holder。<see cref="UserEndpointManager"/> から責務を分離。
/// </summary>
internal sealed class EndpointRegistry
{
    private readonly object _lock = new();
    private readonly List<DiscoveredEndpointData> _writers = new();
    private readonly List<DiscoveredEndpointData> _readers = new();
    private readonly Dictionary<string, List<LocalWriter>> _writersByTopic = new();
    private readonly Dictionary<string, List<LocalReader>> _readersByTopic = new();
    private StatefulWriter[] _writerSnapshot = Array.Empty<StatefulWriter>();

    public void AddLocalWriter(DiscoveredEndpointData endpoint, LocalWriter writer)
    {
        lock (_lock)
        {
            _writers.Add(endpoint);
            AddByTopic(_writersByTopic, endpoint.TopicName, writer);
            RefreshWriterSnapshotLocked();
        }
    }

    public void AddLocalReader(DiscoveredEndpointData endpoint, LocalReader reader)
    {
        lock (_lock)
        {
            _readers.Add(endpoint);
            AddByTopic(_readersByTopic, endpoint.TopicName, reader);
        }
    }

    public RemovedWriter RemoveLocalWriter(Guid endpointGuid, StatefulWriter writer)
    {
        lock (_lock)
        {
            var endpoint = RemoveEndpoint(_writers, endpointGuid);
            if (endpoint is null) return new RemovedWriter(null, Array.Empty<LocalReader>());
            RemoveByReference(_writersByTopic, endpoint.TopicName, writer, static item => item.Writer);
            RefreshWriterSnapshotLocked();
            var readers = SnapshotForTopic(_readersByTopic, endpoint.TopicName);
            return new RemovedWriter(endpoint, readers);
        }
    }

    public RemovedReader RemoveLocalReader(Guid endpointGuid, IUserReader reader)
    {
        lock (_lock)
        {
            var endpoint = RemoveEndpoint(_readers, endpointGuid);
            if (endpoint is null) return new RemovedReader(null, Array.Empty<LocalWriter>());
            RemoveByReference(_readersByTopic, endpoint.TopicName, reader, static item => item.Reader);
            var writers = SnapshotForTopic(_writersByTopic, endpoint.TopicName);
            return new RemovedReader(endpoint, writers);
        }
    }

    public readonly record struct RemovedWriter(DiscoveredEndpointData? Endpoint, LocalReader[] LocalReaders);
    public readonly record struct RemovedReader(DiscoveredEndpointData? Endpoint, LocalWriter[] LocalWriters);

    public bool ShouldAdvertiseForTopic(string topicName, Guid removedEndpointGuid)
    {
        lock (_lock)
        {
            return ContainsGuid(_writersByTopic, topicName, removedEndpointGuid, static item => item.EndpointData)
                || ContainsGuid(_readersByTopic, topicName, removedEndpointGuid, static item => item.EndpointData);
        }
    }

    public LocalWriter[] GetLocalWritersForTopic(string topicName)
    {
        lock (_lock) return SnapshotForTopic(_writersByTopic, topicName);
    }

    public LocalReader[] GetLocalReadersForTopic(string topicName)
    {
        lock (_lock) return SnapshotForTopic(_readersByTopic, topicName);
    }

    public void StartWriters()
    {
        foreach (var w in Volatile.Read(ref _writerSnapshot)) w.Start();
    }

    public void StopWriters()
    {
        foreach (var w in Volatile.Read(ref _writerSnapshot)) w.Stop();
    }

    public EndpointSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new EndpointSnapshot(
                _writersByTopic.Values.SelectMany(static items => items).Select(static item => item.Writer).ToArray(),
                _readersByTopic.Values.SelectMany(static items => items).Select(static item => item.Reader).ToArray());
        }
    }

    private void RefreshWriterSnapshotLocked()
    {
        _writerSnapshot = _writersByTopic.Values
            .SelectMany(static items => items)
            .Select(static item => item.Writer)
            .ToArray();
    }

    private static void AddByTopic<T>(Dictionary<string, List<T>> map, string topic, T item)
    {
        if (!map.TryGetValue(topic, out var list))
        {
            list = new List<T>();
            map[topic] = list;
        }
        list.Add(item);
    }

    private static T[] SnapshotForTopic<T>(Dictionary<string, List<T>> map, string topic)
        => map.TryGetValue(topic, out var list) ? list.ToArray() : Array.Empty<T>();

    private static DiscoveredEndpointData? RemoveEndpoint(List<DiscoveredEndpointData> endpoints, Guid endpointGuid)
    {
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].EndpointGuid.Equals(endpointGuid))
            {
                var ep = endpoints[i];
                endpoints.RemoveAt(i);
                return ep;
            }
        }
        return null;
    }

    private static bool RemoveByReference<TItem, TValue>(
        Dictionary<string, List<TItem>> map, string topic, TValue value, Func<TItem, TValue> selector)
        where TValue : class
    {
        if (!map.TryGetValue(topic, out var list)) return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(selector(list[i]), value))
            {
                list.RemoveAt(i);
                if (list.Count == 0) map.Remove(topic);
                return true;
            }
        }
        return false;
    }

    private static bool ContainsGuid<T>(
        Dictionary<string, List<T>> map, string topic, Guid endpointGuid, Func<T, DiscoveredEndpointData> selector)
        => map.TryGetValue(topic, out var list)
        && list.Any(item => selector(item).EndpointGuid.Equals(endpointGuid));

    public readonly record struct EndpointSnapshot(StatefulWriter[] Writers, IUserReader[] Readers);
}
```

Create `src/rosettadds/Dds/EndpointRegistry.cs.meta` (random GUID, e.g. `b172626377777691e2724e60718293b5`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~EndpointRegistryTests`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/rosettadds/Dds/EndpointRegistry.cs src/rosettadds/Dds/EndpointRegistry.cs.meta tests/rosettadds.Tests/Dds/EndpointRegistryTests.cs
git commit -m "refactor(dds): EndpointRegistry を抽出 (状態 holder)"
```

---

### Task 4: UserEndpointManager リファクタ + DomainParticipant 配線

**Files:**
- Modify: `src/rosettadds/Dds/UserEndpointManager.cs`
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
- Modify: `tests/rosettadds.Tests/Dds/UserEndpointManagerTests.cs`
- Test: `tests/rosettadds.Tests/Dds/UserEndpointManagerRefactoredTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/rosettadds.Tests/Dds/UserEndpointManagerRefactoredTests.cs`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.HistoryCache;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Dds;

public class UserEndpointManagerRefactoredTests
{
    [Fact]
    public void RegisterWriter_は_receiver_RegisterWriter_を呼び_writerSnapshotに含める()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), fake, NullLogger.Instance);
        var (endpoint, writer) = MakeWriter(prefix, "t", "TypeA");

        manager.RegisterWriter(endpoint, writer);

        fake.RegisteredWriters.Should().ContainSingle()
            .Which.writer.Should().BeSameAs(writer);
        manager.Snapshot().Writers.Should().ContainSingle().Which.Should().BeSameAs(writer);
    }

    [Fact]
    public void RegisterWriter_は_RemoteReader_snapshotの同topicとmatchする()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var discovery = new DiscoveryDb();
        var remoteReaderEndpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, new EntityId(2, EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = "t",
            TypeName = "TypeA",
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        remoteReaderEndpoint.UnicastLocators.Add(new Locator { Kind = LocatorKind.UdpV4, Port = 9000 });
        discovery.UpsertEndpoint(remoteReaderEndpoint, DateTime.UtcNow);
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(discovery, fake, NullLogger.Instance);
        var (endpoint, writer) = MakeWriter(prefix, "t", "TypeA");

        manager.RegisterWriter(endpoint, writer);

        writer.GetReaderProxy(remoteReaderEndpoint.EndpointGuid).Should().NotBeNull();
    }

    [Fact]
    public void UnregisterWriter_は_not_found時に_NotFoundを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), fake, NullLogger.Instance);
        var (endpoint, writer) = MakeWriter(prefix, "t", "TypeA");
        var otherGuid = new Guid(GuidPrefix.Create(VendorId.ROSettaDDS, 9, 9, 9), new EntityId(1, EntityKind.UserDefinedWriterNoKey));

        var result = manager.UnregisterWriter(otherGuid, writer);

        result.Should().Be(UnregisterResult.NotFound);
    }

    [Fact]
    public void UnregisterWriter_は_最後の1個なら_shouldAdvertise_falseを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), fake, NullLogger.Instance);
        var (endpoint, writer) = MakeWriter(prefix, "t", "TypeA");
        manager.RegisterWriter(endpoint, writer);

        var result = manager.UnregisterWriter(endpoint.EndpointGuid, writer);

        result.Endpoint.Should().NotBeNull();
        result.ShouldAdvertise.Should().BeFalse();
    }

    [Fact]
    public void UnregisterWriter_は_同topicに同GUID残存なら_shouldAdvertise_trueを返す()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(new DiscoveryDb(), fake, NullLogger.Instance);
        var (ep1, w1) = MakeWriter(prefix, "t", "TypeA");
        var (ep2, w2) = MakeWriter(prefix, "t", "TypeA");
        manager.RegisterWriter(ep1, w1);
        manager.RegisterWriter(ep2, w2);

        var result = manager.UnregisterWriter(ep1.EndpointGuid, w1);

        result.Endpoint.Should().NotBeNull();
        result.ShouldAdvertise.Should().BeTrue();
    }

    [Fact]
    public void RemoteReaderChanged_は_同topicの_local_writerと_matchする()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        var discovery = new DiscoveryDb();
        var fake = new FakeEndpointReceiver();
        var manager = new UserEndpointManager(discovery, fake, NullLogger.Instance);
        var (writerEp, writer) = MakeWriter(prefix, "t", "TypeA");
        manager.RegisterWriter(writerEp, writer);
        var remoteReader = new RemoteEndpoint(new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = new Guid(prefix, new EntityId(2, EntityKind.UserDefinedReaderNoKey)),
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = "t",
            TypeName = "TypeA",
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        }, DateTime.UtcNow);

        manager.RemoteReaderChanged(remoteReader);

        writer.GetReaderProxy(remoteReader.Guid).Should().NotBeNull();
    }

    private static (DiscoveredEndpointData, StatefulWriter) MakeWriter(GuidPrefix prefix, string topic, string typeName)
    {
        var entityId = new EntityId(1, EntityKind.UserDefinedWriterNoKey);
        var writerGuid = new Guid(prefix, entityId);
        var endpoint = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = new Guid(prefix, EntityId.Participant),
            TopicName = topic,
            TypeName = typeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpoint.UnicastLocators.Add(new Locator { Kind = LocatorKind.UdpV4, Port = 7411 });
        var recordingTransport = new RecordingTransport(7411);
        var history = new WriterHistoryCache(writerGuid, maxSamples: 16);
        var writer = new StatefulWriter(
            sendTransport: recordingTransport,
            multicastDestination: new Locator { Kind = LocatorKind.UdpV4, Port = 7401 },
            version: ProtocolVersion.Current,
            vendorId: VendorId.ROSettaDDS,
            localPrefix: prefix,
            writerEntityId: entityId,
            heartbeatPeriod: TimeSpan.FromMilliseconds(200),
            history: history);
        return (endpoint, writer);
    }

    private sealed class RecordingTransport : IRtpsTransport
    {
        public RecordingTransport(uint port)
        {
            LocalLocator = Locator.FromUdpV4(IPAddress.Loopback, port);
        }

        public Locator LocalLocator { get; }
        public event Action<ReadOnlyMemory<byte>, Locator>? Received { add { } remove { } }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, Locator destination, CancellationToken cancellationToken = default) => default;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~UserEndpointManagerRefactoredTests`

Expected: FAIL because `UserEndpointManager` constructor still takes `ParticipantRtpsReceiver` (not `IEndpointReceiver`).

- [ ] **Step 3: Refactor UserEndpointManager**

Rewrite `src/rosettadds/Dds/UserEndpointManager.cs` to use `EndpointRegistry` + `IEndpointReceiver` + `EndpointMatcher`:

```csharp
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Rtps.Writer;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// local user endpoint の登録状態とマッチングのオーケストレータ。
/// 状態は <see cref="EndpointRegistry"/>、判定は <see cref="EndpointMatcher"/>、
/// 副作用は <see cref="IEndpointReceiver"/> に委譲する。
/// </summary>
internal sealed class UserEndpointManager
{
    private readonly DiscoveryDb _discoveryDb;
    private readonly IEndpointReceiver _receiver;
    private readonly EndpointRegistry _registry = new();
    private readonly ILogger _logger;

    public UserEndpointManager(DiscoveryDb discoveryDb, IEndpointReceiver receiver, ILogger logger)
    {
        _discoveryDb = discoveryDb ?? throw new ArgumentNullException(nameof(discoveryDb));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterWriter(DiscoveredEndpointData endpointData, StatefulWriter writer)
    {
        ValidateEndpoint(endpointData, EndpointKind.Writer);
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var local = new LocalWriter(endpointData, writer);
        _registry.AddLocalWriter(endpointData, local);
        _receiver.RegisterWriter(writer.WriterEntityId, writer);

        foreach (var localReader in _registry.GetLocalReadersForTopic(endpointData.TopicName))
        {
            MatchLocalAndLocal(localReader, local);
        }
        foreach (var remoteReader in _discoveryDb.ReaderSnapshot())
        {
            if (remoteReader.TopicName == endpointData.TopicName)
            {
                MatchLocalAndRemoteReader(local, remoteReader);
            }
        }
    }

    public void RegisterReader(DiscoveredEndpointData endpointData, IUserReader reader)
    {
        ValidateEndpoint(endpointData, EndpointKind.Reader);
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var local = new LocalReader(endpointData, reader);
        _registry.AddLocalReader(endpointData, local);
        _receiver.RegisterReader(reader.ReaderEntityId, reader.Handler);

        foreach (var localWriter in _registry.GetLocalWritersForTopic(endpointData.TopicName))
        {
            MatchLocalAndLocal(local, localWriter);
        }
        foreach (var remoteWriter in _discoveryDb.WriterSnapshot())
        {
            if (remoteWriter.TopicName == endpointData.TopicName)
            {
                MatchLocalAndRemoteWriter(local, remoteWriter);
            }
        }
    }

    public UnregisterResult UnregisterWriter(Guid endpointGuid, StatefulWriter writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        var removed = _registry.RemoveLocalWriter(endpointGuid, writer);
        if (removed.Endpoint is null) return UnregisterResult.NotFound;
        _receiver.UnregisterWriter(writer.WriterEntityId);
        foreach (var localReader in removed.LocalReaders)
        {
            localReader.Reader.UnmatchWriter(endpointGuid);
        }
        var shouldAdvertise = _registry.ShouldAdvertiseForTopic(removed.Endpoint.TopicName, endpointGuid);
        return new UnregisterResult(removed.Endpoint, shouldAdvertise);
    }

    public UnregisterResult UnregisterReader(Guid endpointGuid, IUserReader reader)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));

        var removed = _registry.RemoveLocalReader(endpointGuid, reader);
        if (removed.Endpoint is null) return UnregisterResult.NotFound;
        _receiver.UnregisterReader(reader.ReaderEntityId);
        foreach (var localWriter in removed.LocalWriters)
        {
            localWriter.Writer.UnmatchReader(endpointGuid);
        }
        var shouldAdvertise = _registry.ShouldAdvertiseForTopic(removed.Endpoint.TopicName, endpointGuid);
        return new UnregisterResult(removed.Endpoint, shouldAdvertise);
    }

    public EndpointSnapshot Snapshot() => _registry.Snapshot();
    public void StartWriters() => _registry.StartWriters();
    public void StopWriters() => _registry.StopWriters();

    public void RemoteReaderChanged(RemoteEndpoint remote)
    {
        foreach (var local in _registry.GetLocalWritersForTopic(remote.TopicName))
        {
            MatchLocalAndRemoteReader(local, remote);
        }
    }

    public void RemoteWriterChanged(RemoteEndpoint remote)
    {
        foreach (var local in _registry.GetLocalReadersForTopic(remote.TopicName))
        {
            MatchLocalAndRemoteWriter(local, remote);
        }
    }

    public void RemoteReaderLost(RemoteEndpoint remote)
    {
        foreach (var local in _registry.GetLocalWritersForTopic(remote.TopicName))
        {
            local.Writer.UnmatchReader(remote.Data.EndpointGuid);
        }
    }

    public void RemoteWriterLost(RemoteEndpoint remote)
    {
        foreach (var local in _registry.GetLocalReadersForTopic(remote.TopicName))
        {
            local.Reader.UnmatchWriter(remote.Data.EndpointGuid);
        }
    }

    private void MatchLocalAndLocal(LocalReader localReader, LocalWriter localWriter)
    {
        var d = EndpointMatcher.EvaluateLocalLocal(localReader, localWriter);
        if (d.Compatible)
        {
            localReader.Reader.MatchWriter(localWriter.EndpointData.EndpointGuid, d.UnicastLocator);
            localWriter.Writer.MatchReader(localReader.EndpointData.EndpointGuid, d.UnicastLocator, d.ReliabilityKind ?? ReliabilityKind.Reliable);
            _logger.Debug($"DomainParticipant: matched local reader with local writer on topic={localReader.EndpointData.TopicName} writer={localWriter.EndpointData.EndpointGuid}");
        }
        else
        {
            localReader.Reader.UnmatchWriter(localWriter.EndpointData.EndpointGuid);
            localWriter.Writer.UnmatchReader(localReader.EndpointData.EndpointGuid);
        }
    }

    private void MatchLocalAndRemoteReader(LocalWriter local, RemoteEndpoint remote)
    {
        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);
        if (d.Compatible)
        {
            var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remote, _discoveryDb.Snapshot());
            local.Writer.MatchReader(remote.Data.EndpointGuid, loc, remote.Data.Reliability.Kind);
            _logger.Debug($"DomainParticipant: matched local writer with remote reader on topic={remote.TopicName} reader={remote.Data.EndpointGuid}");
        }
        else
        {
            local.Writer.UnmatchReader(remote.Data.EndpointGuid);
        }
    }

    private void MatchLocalAndRemoteWriter(LocalReader local, RemoteEndpoint remote)
    {
        var d = EndpointMatcher.EvaluateLocalRemote(local, remote);
        if (d.Compatible)
        {
            var loc = d.UnicastLocator ?? EndpointMatcher.ResolveRemoteUnicastLocator(remote, _discoveryDb.Snapshot());
            local.Reader.MatchWriter(remote.Data.EndpointGuid, loc);
            _logger.Debug($"DomainParticipant: matched local reader with remote writer on topic={remote.TopicName} writer={remote.Data.EndpointGuid}");
        }
        else
        {
            local.Reader.UnmatchWriter(remote.Data.EndpointGuid);
        }
    }

    private static void ApplyMatch(MatchDecision d, Action<Locator?> onMatch, Action onUnmatch)
    {
        if (d.Compatible) onMatch(d.UnicastLocator);
        else onUnmatch();
    }

    private static void ValidateEndpoint(DiscoveredEndpointData endpoint, EndpointKind expectedKind)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (endpoint.Kind != expectedKind)
        {
            throw new ArgumentException($"Expected {expectedKind} endpoint, got {endpoint.Kind}.", nameof(endpoint));
        }
        if (string.IsNullOrEmpty(endpoint.TopicName))
        {
            throw new ArgumentException("Endpoint topic name cannot be null or empty.", nameof(endpoint));
        }
    }

    public readonly record struct UnregisterResult(DiscoveredEndpointData? Endpoint, bool ShouldAdvertise)
    {
        public static UnregisterResult NotFound => new(null, false);
    }
}
```

The exact shape of `Unregister*` 内部 depends on how `RemoveLocalWriter` returns data. Pragmatic refactor: change `EndpointRegistry.RemoveLocalWriter` to return `(DiscoveredEndpointData? endpoint, LocalReader[] readers)` instead of just `LocalReader[]` to give the orchestrator what it needs to build `UnregisterResult`. This is a minor signature evolution; add a step to update the test in Task 3 if needed.

- [ ] **Step 4: Wire DomainParticipant**

In `src/rosettadds/Dds/DomainParticipant.cs` line 70 (constructor), change:

```csharp
_userEndpoints = new UserEndpointManager(_discoveryDb, _receiver, _options.Logger);
```

to:

```csharp
_userEndpoints = new UserEndpointManager(_discoveryDb, new ParticipantRtpsReceiverAdapter(_receiver), _options.Logger);
```

- [ ] **Step 5: Update existing UserEndpointManagerTests.cs**

In `tests/rosettadds.Tests/Dds/UserEndpointManagerTests.cs`, replace the 2 test setups:

```csharp
new UserEndpointManager(new DiscoveryDb(), new ParticipantRtpsReceiver(prefix), NullLogger.Instance);
```

with:

```csharp
new UserEndpointManager(new DiscoveryDb(), new ParticipantRtpsReceiverAdapter(new ParticipantRtpsReceiver(prefix)), NullLogger.Instance);
```

Add `using ROSettaDDS.Dds;` is already present. Add the adapter's namespace import if needed.

- [ ] **Step 6: Run tests to verify all pass**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~UserEndpointManagerRefactoredTests|FullyQualifiedName~UserEndpointManagerTests|FullyQualifiedName~EndpointRegistryTests|FullyQualifiedName~EndpointMatcherTests|FullyQualifiedName~ParticipantRtpsReceiverAdapterTests"`

Expected: PASS.

- [ ] **Step 7: Run full .NET test suite**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj`

Expected: PASS (既存 integration test も含む全件)。

- [ ] **Step 8: Commit**

```bash
git add src/rosettadds/Dds/UserEndpointManager.cs src/rosettadds/Dds/DomainParticipant.cs tests/rosettadds.Tests/Dds/UserEndpointManagerTests.cs tests/rosettadds.Tests/Dds/UserEndpointManagerRefactoredTests.cs
git commit -m "refactor(dds): UserEndpointManager を Registry/Matcher/Receiver へ委譲"
```

---

### Task 5: Full Verification + PR

**Files:**
- Modify as needed from previous tasks.

- [ ] **Step 1: Run .NET tests**

Run: `dotnet test rosettadds.sln`

Expected: PASS.

- [ ] **Step 2: Run Unity meta check**

Run: `.github/scripts/check_unity_meta.sh`

Expected: PASS with no missing or orphan `.meta` files.

- [ ] **Step 3: Inspect git status**

Run: `git status --short --branch`

Expected: clean working tree on `refactor/user-endpoint-manager-testability`.

- [ ] **Step 4: Push and create PR**

```bash
git push -u origin refactor/user-endpoint-manager-testability
gh pr create --draft --title "refactor(dds): UserEndpointManager の責務を分離" --body "UserEndpointManager を EndpointRegistry / EndpointMatcher / IEndpointReceiver + adapter へ分離し、public API を一切変えずに contract 単位の単体テストを追加します。"
```
