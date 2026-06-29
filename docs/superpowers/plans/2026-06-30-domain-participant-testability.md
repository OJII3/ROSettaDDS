# DomainParticipant Testability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `DomainParticipant` から endpoint 生成、SEDP 広告、lease 監視を分離し、契約単位でテストできるようにする。

**Architecture:** `ParticipantEndpointFactory` が user endpoint の生成を担当し、`SedpEndpointAdvertiser` が SEDP 副作用を担当し、`LeaseExpiryMonitor` が lease expiry loop を担当する。`DomainParticipant` は公開 API とライフサイクル統括だけに寄せる。

**Tech Stack:** C# / .NET, xUnit, FluentAssertions, Unity package under `src/rosettadds` with `.meta` files.

---

## File Structure

- Create: `src/rosettadds/Dds/ParticipantEndpointFactory.cs`
  - Writer / reader / endpoint data の生成を担当する internal class。
- Create: `src/rosettadds/Dds/SedpEndpointAdvertiser.cs`
  - SEDP add / unregister の非同期副作用、例外処理、timeout を担当する internal class。
- Create: `src/rosettadds/Dds/LeaseExpiryMonitor.cs`
  - lease expiry loop の周期計算、開始、停止を担当する internal class。
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
  - 新しい internal components を使うように公開 API 内部を置き換える。
- Test: `tests/rosettadds.Tests/Dds/ParticipantEndpointFactoryTests.cs`
- Test: `tests/rosettadds.Tests/Dds/SedpEndpointAdvertiserTests.cs`
- Test: `tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs`
- Create: `src/rosettadds/Dds/*.cs.meta`
  - Unity import 用 meta files。

---

### Task 1: LeaseExpiryMonitor

**Files:**
- Create: `src/rosettadds/Dds/LeaseExpiryMonitor.cs`
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
- Test: `tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;

namespace ROSettaDDS.Tests.Dds;

public class LeaseExpiryMonitorTests
{
    [Fact]
    public void CheckPeriod_は_SpdpInterval_と_LeaseDuration_の短い正値を使い_下限を適用する()
    {
        var options = new DomainParticipantOptions
        {
            SpdpInterval = TimeSpan.FromMilliseconds(20),
            LeaseDuration = DurationQos.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
        };

        LeaseExpiryMonitor.ComputeCheckPeriod(options)
            .Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void CheckPeriod_は_最大1秒を超えない()
    {
        var options = new DomainParticipantOptions
        {
            SpdpInterval = TimeSpan.FromSeconds(10),
            LeaseDuration = DurationQos.FromTimeSpan(TimeSpan.FromSeconds(8)),
        };

        LeaseExpiryMonitor.ComputeCheckPeriod(options)
            .Should().Be(TimeSpan.FromSeconds(1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~LeaseExpiryMonitorTests`

Expected: FAIL because `LeaseExpiryMonitor` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `LeaseExpiryMonitor` with `ComputeCheckPeriod`, `Start`, `Stop`, and `Dispose`. Move the existing constants and loop code from `DomainParticipant` into this class.

- [ ] **Step 4: Wire DomainParticipant**

Replace `_leaseExpiryCts`, `_leaseExpiryLoop`, `StartLeaseExpiryLoop`, `StopLeaseExpiryLoop`, `LeaseExpiryLoopAsync`, and `ComputeLeaseExpiryCheckPeriod` with one `_leaseExpiryMonitor` field.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~LeaseExpiryMonitorTests`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/rosettadds/Dds/LeaseExpiryMonitor.cs src/rosettadds/Dds/LeaseExpiryMonitor.cs.meta src/rosettadds/Dds/DomainParticipant.cs tests/rosettadds.Tests/Dds/LeaseExpiryMonitorTests.cs
git commit -m "refactor(dds): lease 監視を DomainParticipant から分離"
```

### Task 2: SedpEndpointAdvertiser

**Files:**
- Create: `src/rosettadds/Dds/SedpEndpointAdvertiser.cs`
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
- Test: `tests/rosettadds.Tests/Dds/SedpEndpointAdvertiserTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class SedpEndpointAdvertiserTests
{
    [Fact]
    public async Task RunAsync_は_cancellation済みOperationCanceledExceptionを抑制する()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var advertiser = new SedpEndpointAdvertiser(NullLogger.Instance, () => cts.Token);

        var act = async () => await advertiser.RunAsync(
            _ => throw new OperationCanceledException(cts.Token),
            "failed");

        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~SedpEndpointAdvertiserTests`

Expected: FAIL because `SedpEndpointAdvertiser` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `SedpEndpointAdvertiser` with `RunAsync(Func<CancellationToken, ValueTask>, string)` and `WaitForUnregister(ValueTask)`.

- [ ] **Step 4: Wire DomainParticipant**

Replace `RunSedpOperationAsync` and `WaitForSedpUnregister` calls with `_sedpAdvertiser`.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~SedpEndpointAdvertiserTests`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/rosettadds/Dds/SedpEndpointAdvertiser.cs src/rosettadds/Dds/SedpEndpointAdvertiser.cs.meta src/rosettadds/Dds/DomainParticipant.cs tests/rosettadds.Tests/Dds/SedpEndpointAdvertiserTests.cs
git commit -m "refactor(dds): SEDP 広告処理を分離"
```

### Task 3: ParticipantEndpointFactory

**Files:**
- Create: `src/rosettadds/Dds/ParticipantEndpointFactory.cs`
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`
- Test: `tests/rosettadds.Tests/Dds/ParticipantEndpointFactoryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Dds.QoS;
using ROSettaDDS.Discovery;
using ROSettaDDS.Msgs.Std;

namespace ROSettaDDS.Tests.Dds;

public class ParticipantEndpointFactoryTests
{
    [Fact]
    public void CreateWriter_は_endpoint_dataにQoSとlocatorを設定する()
    {
        var prefix = GuidPrefix.Create(VendorId.ROSettaDDS, 1, 2, 3);
        using var participant = new DomainParticipant(new DomainParticipantOptions
        {
            LocalhostOnly = true,
            ParticipantId = 10,
        });
        var factory = ParticipantEndpointFactory.ForParticipant(participant);

        var result = factory.CreateWriter(
            "rt/chatter",
            "chatter",
            StringMessageSerializer.Instance,
            ReliabilityQos.BestEffort,
            DurabilityQos.Volatile,
            StringMessage.DdsTypeName);

        result.EndpointData.Kind.Should().Be(EndpointKind.Writer);
        result.EndpointData.TopicName.Should().Be("rt/chatter");
        result.EndpointData.TypeName.Should().Be(StringMessage.DdsTypeName);
        result.EndpointData.Reliability.Kind.Should().Be(ReliabilityKind.BestEffort);
        result.EndpointData.UnicastLocators.Should().NotBeEmpty();
        result.EndpointData.MulticastLocators.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter FullyQualifiedName~ParticipantEndpointFactoryTests`

Expected: FAIL because `ParticipantEndpointFactory` does not exist.

- [ ] **Step 3: Write minimal implementation**

Extract writer / reader / reliable reply reader creation from `DomainParticipant` into `ParticipantEndpointFactory`.

- [ ] **Step 4: Wire DomainParticipant**

`CreateWriterInternal`, `CreateSubscription`, and `CreateReliableReplyReaderInternal` should ask the factory for endpoint objects, then register and advertise them.

- [ ] **Step 5: Run focused tests**

Run: `dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --filter "FullyQualifiedName~ParticipantEndpointFactoryTests|FullyQualifiedName~PublisherSubscriptionMatchedTests|FullyQualifiedName~ServiceClientLoopbackTests"`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/rosettadds/Dds/ParticipantEndpointFactory.cs src/rosettadds/Dds/ParticipantEndpointFactory.cs.meta src/rosettadds/Dds/DomainParticipant.cs tests/rosettadds.Tests/Dds/ParticipantEndpointFactoryTests.cs
git commit -m "refactor(dds): user endpoint 生成を factory に分離"
```

### Task 4: Full Verification

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

Expected: clean working tree on `refactor/domain-participant-testability`.

- [ ] **Step 4: Push and create PR**

```bash
git push -u origin refactor/domain-participant-testability
gh pr create --draft --title "refactor(dds): DomainParticipant の責務を分離" --body "DomainParticipant から endpoint 生成、SEDP 広告、lease 監視を分離し、単体テストを追加します。"
```
