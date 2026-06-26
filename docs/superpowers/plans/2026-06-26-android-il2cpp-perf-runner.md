# Android IL2CPP Performance Runner 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 既存の `tools/rosettadds-perf-runner` を、Android 実機 (IL2CPP ビルド) 上で Unity Player ハーネスを動かし、LAN 上の ROS 2 helper と pub/sub 計測できるところまで拡張する。既存 desktop 経路 (StandaloneLinux64 / StandaloneOSX) は無改修で並走する。

**Architecture:** 既存の `ProcessCapture` を使った desktop 経路を `IProcessDriver` 抽象でくるみ、Android 用の `AndroidAdbDriver` を新設する。Player 側 CLI に `--rosettadds-localhost-only` を追加し、Android 経路では `false` を注入する。Editor 側の `ROSettaDDSPerfPlayerBuilder` に `BuildTarget.Android` + `NamedBuildTarget.Android` 経路を追加し、`applicationIdentifier.Android` を `com.ojii3.rosettadds.perf` に固定する。helper 起動時の `ROS_LOCALHOST_ONLY` を build target 連動で 0/1 切替する。TDD は runner 側 .NET 8 プロジェクト、EditMode 側は Unity EditMode test で分離して回す。

**Tech Stack:** C# / .NET 8 / xunit + FluentAssertions / Unity 6000.3 EditMode (NUnit) / ADB (android-tools 35.0.2 from nix devShell) / Ros2Unity Project.

**Design doc:** `docs/superpowers/specs/2026-06-26-android-il2cpp-perf-runner-design.md`

---

## 変更ファイル一覧

| 操作 | パス | 役割 |
| --- | --- | --- |
| Create | `tools/rosettadds-perf-runner/IProcessDriver.cs` | プロセス起動・同期・回収の interface |
| Create | `tools/rosettadds-perf-runner/LaunchSpec.cs` | 起動 spec record + `LogKind` enum |
| Create | `tools/rosettadds-perf-runner/DesktopProcessDriver.cs` | desktop 経路の `IProcessDriver` 実装 |
| Create | `tools/rosettadds-perf-runner/AdbClient.cs` | adb subprocess の薄い interface + 実装 |
| Create | `tools/rosettadds-perf-runner/AndroidAdbDriver.cs` | Android 経路の `IProcessDriver` 実装 |
| Modify | `tools/rosettadds-perf-runner/RunnerOptions.cs` | `--build-target Android` / `--adb` / `--android-device` / `--android-package` / `--android-activity` 追加 |
| Modify | `tools/rosettadds-perf-runner/Program.cs` | `IProcessDriver` 経由に refactor、build target 分岐、helper env 切替 |
| Modify | `rosettadds.sln` | 新 test project を追加 |
| Create | `tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj` | xUnit プロジェクト |
| Create | `tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` | CLI フラグ parse テスト |
| Create | `tools/rosettadds-perf-runner.Tests/AdbClientTests.cs` | adb コマンドライン組み立てテスト |
| Create | `tools/rosettadds-perf-runner.Tests/AndroidAdbDriverTests.cs` | driver 動作テスト (FakeAdbClient) |
| Create | `tools/rosettadds-perf-runner.Tests/ProgramTests.cs` | scenario 統合テスト (Fake ドライバ) |
| Create | `tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs` | adb 呼び出し記録用 fake |
| Create | `tools/rosettadds-perf-runner.Tests/Fakes/FakeProcessDriver.cs` | `IProcessDriver` fake |
| Modify | `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs` | `--rosettadds-localhost-only` 追加 |
| Modify | `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs` | `args.LocalhostOnly` 反映 (241 行 hardcoded 置換) |
| Create | `Ros2Unity/Assets/Tests/EditMode/PerfPlayerArgumentsTests.cs` | EditMode での parse テスト |
| Modify | `Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs` | `BuildTarget.Android` + `NamedBuildTarget.Android` 経路追加 |
| Modify | `Ros2Unity/ProjectSettings/ProjectSettings.asset` | `applicationIdentifier.Android: com.ojii3.rosettadds.perf` |

discovery / SEDP / RTPS / CDR / transport 層は触らない。

---

## Task 1: runner 用 xUnit テストプロジェクトを新設

**Files:**
- Create: `tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj`
- Modify: `rosettadds.sln`

- [ ] **Step 1.1: csproj を作成**

`tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj` を新規作成し、以下を記述する:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>ROSettaDDS.PerfRunner.Tests</RootNamespace>
    <AssemblyName>rosettadds-perf-runner.Tests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\rosettadds-perf-runner\rosettadds-perf-runner.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 1.2: 動作確認用の空テストを追加**

`tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` を新規作成し、最初は仮の 1 テストだけ置く:

```csharp
using FluentAssertions;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void 仮テスト()
    {
        true.Should().BeTrue();
    }
}
```

- [ ] **Step 1.3: rosettadds.sln に追加**

`rosettadds.sln` を開き、`tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj` の Project 行と同じ書式で `tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj` を追加する。次に同 sln 内の `GlobalSection(ProjectConfigurationPlatforms)` にも同 project の Debug|Any CPU / Release|Any CPU 行を既存テスト (`tests/rosettadds.Tests/rosettadds.Tests.csproj`) の行を複製して `Debug|Any CPU` の中身を `<project guid>.Debug|Any CPU.ActiveCfg = Debug|Any CPU` / `Debug|Any CPU.Build.0 = Debug|Any CPU` の 2 行ずつ `Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = ... ` のブロック直後に並べる。guid は新規生成 (`dotnet new` 等で OK、PowerShell なら `[guid]::NewGuid()`)。

> 実装メモ: `dotnet sln add tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj` を実行しても sln への登録は可能だが、`GlobalSection(ProjectConfigurationPlatforms)` 内の ActiveCfg / Build.0 行は自動では出ないので、手動で既存テストプロジェクトのエントリをコピーして `<NewProjectGuid>` を 1 個所に置換して並べる。

- [ ] **Step 1.4: dotnet test で動作確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --nologo
```

期待: `Passed ROSettaDDS.PerfRunner.Tests.RunnerOptionsTests.仮テスト` が 1 件、合計 1 passed, 0 failed。

- [ ] **Step 1.5: コミット**

```bash
git add tools/rosettadds-perf-runner.Tests/ rosettadds.sln
git commit -m "chore(runner): perf-runner 用 xUnit テストプロジェクトを新設"
```

---

## Task 2: RunnerOptions に `--build-target Android` を追加 (TDD)

**Files:**
- Modify: `tools/rosettadds-perf-runner/RunnerOptions.cs`
- Modify: `tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs`

- [ ] **Step 2.1: 失敗するテストを追加**

`tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs` の `仮テスト` 直後に追加:

```csharp
    [Fact]
    public void BuildTarget_既定値は_OS_依存()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        var expected = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX)
            ? "StandaloneOSX" : "StandaloneLinux64";
        options.BuildTarget.Should().Be(expected);
    }

    [Fact]
    public void BuildTarget_Android_を_受理する()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        options.BuildTarget.Should().Be("Android");
    }

    [Fact]
    public void BuildTarget_未知の値は_例外()
    {
        var act = () => RunnerOptions.Parse(new[] { "--build-target", "Bogus" });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*--build-target*StandaloneLinux64*StandaloneOSX*Android*");
    }
```

- [ ] **Step 2.2: テスト失敗確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~RunnerOptionsTests" --nologo
```

期待: `BuildTarget_Android_を_受理する` が `ArgumentException: --build-target must be StandaloneLinux64 or StandaloneOSX` で FAIL。

- [ ] **Step 2.3: 実装 (最小)**

`tools/rosettadds-perf-runner/RunnerOptions.cs:74-77` を以下に置換:

```csharp
        if (options.BuildTarget != "StandaloneLinux64"
            && options.BuildTarget != "StandaloneOSX"
            && options.BuildTarget != "Android")
        {
            throw new ArgumentException(
                "--build-target must be StandaloneLinux64, StandaloneOSX, or Android");
        }
```

- [ ] **Step 2.4: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~RunnerOptionsTests" --nologo
```

期待: 4 件すべて PASS (`仮テスト` 含む)。

- [ ] **Step 2.5: コミット**

```bash
git add tools/rosettadds-perf-runner/RunnerOptions.cs tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs
git commit -m "feat(runner): RunnerOptions に --build-target Android を追加"
```

---

## Task 3: RunnerOptions に ADB 関連フラグを追加 (TDD)

**Files:**
- Modify: `tools/rosettadds-perf-runner/RunnerOptions.cs`
- Modify: `tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs`

- [ ] **Step 3.1: 失敗するテストを追加**

`RunnerOptionsTests.cs` に追加:

```csharp
    [Fact]
    public void Adb_既定値は_adb()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.Adb.Should().Be("adb");
    }

    [Fact]
    public void Adb_カスタムパス_が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--adb", "/opt/adb/bin/adb" });
        options.Adb.Should().Be("/opt/adb/bin/adb");
    }

    [Fact]
    public void AndroidDevice_省略時_null()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidDevice.Should().BeNull();
    }

    [Fact]
    public void AndroidDevice_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-device", "ABCDEFG" });
        options.AndroidDevice.Should().Be("ABCDEFG");
    }

    [Fact]
    public void AndroidPackage_既定値は_com_ojii3_rosettadds_perf()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidPackage.Should().Be("com.ojii3.rosettadds.perf");
    }

    [Fact]
    public void AndroidPackage_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-package", "com.example.foo" });
        options.AndroidPackage.Should().Be("com.example.foo");
    }

    [Fact]
    public void AndroidActivity_既定値は_Unity6_の_GameActivity()
    {
        var options = RunnerOptions.Parse(Array.Empty<string>());
        options.AndroidActivity.Should().Be("com.unity3d.player.GameActivity");
    }

    [Fact]
    public void AndroidActivity_指定が_保持される()
    {
        var options = RunnerOptions.Parse(new[] { "--android-activity", "com.example.MainActivity" });
        options.AndroidActivity.Should().Be("com.example.MainActivity");
    }
```

- [ ] **Step 3.2: テスト失敗確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~RunnerOptionsTests" --nologo
```

期待: 8 件全て compile error (`Adb` / `AndroidDevice` / `AndroidPackage` / `AndroidActivity` が存在しない) で FAIL。

- [ ] **Step 3.3: 実装**

`RunnerOptions.cs` に追加:
- class 内に `internal string Adb { get; private set; } = "adb";` `internal string? AndroidDevice { get; private set; }` `internal string AndroidPackage { get; private set; } = "com.ojii3.rosettadds.perf";` `internal string AndroidActivity { get; private set; } = "com.unity3d.player.GameActivity";` を追加。
- `Parse` 内の switch に `case "--adb":` / `case "--android-device":` / `case "--android-package":` / `case "--android-activity":` を `RequireValue(args, ref i, arg)` 経由で処理する 4 行を追加。
- `PrintHelp` に 4 行追加:

```text
  --adb <path>                               Default: adb (PATH 解決)
  --android-device <serial>                  Default: adb devices -l の単独エントリ。複数時はエラー。
  --android-package <id>                     Default: com.ojii3.rosettadds.perf
  --android-activity <component>             Default: com.unity3d.player.GameActivity
```

- [ ] **Step 3.4: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~RunnerOptionsTests" --nologo
```

期待: 12 件すべて PASS。

- [ ] **Step 3.5: コミット**

```bash
git add tools/rosettadds-perf-runner/RunnerOptions.cs tools/rosettadds-perf-runner.Tests/RunnerOptionsTests.cs
git commit -m "feat(runner): RunnerOptions に --adb / --android-* 系フラグを追加"
```

---

## Task 4: PerfPlayerArguments に `--rosettadds-localhost-only` を追加 (TDD)

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfPlayerArguments.cs`
- Create: `Ros2Unity/Assets/Tests/EditMode/PerfPlayerArgumentsTests.cs`

- [ ] **Step 4.1: 既存 PerfPlayerArguments.cs を読む**

`Ros2Unity/Assets/Perf/PerfPlayerArguments.cs` を全文読み、`Parsed` struct / `ReadBool` / `ReadRequired` / `ReadInt` 等の既存 helper のシグネチャを確認。`--rosettadds-release-file` のような optional フラグの読み取りパターンを真似る。

- [ ] **Step 4.2: 失敗する EditMode テストを追加**

`Ros2Unity/Assets/Tests/EditMode/PerfPlayerArgumentsTests.cs` を新規作成し、以下を記述する:

```csharp
using System;
using FluentAssertions;
using NUnit.Framework;
using ROSettaDDS.UnityPerfHarness;

namespace ROSettaDDS.UnityPerfHarness.Tests
{
    public class PerfPlayerArgumentsTests
    {
        private static string[] Args(params string[] a) => a;

        [Test]
        public void LocalhostOnly_未指定なら_true()
        {
            PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2"),
                out var parsed, out _).Should().BeTrue();
            parsed.LocalhostOnly.Should().BeTrue();
        }

        [Test]
        public void LocalhostOnly_false_を_受理する()
        {
            PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2",
                     "--rosettadds-localhost-only", "false"),
                out var parsed, out _).Should().BeTrue();
            parsed.LocalhostOnly.Should().BeFalse();
        }

        [Test]
        public void LocalhostOnly_true_を_受理する()
        {
            PerfPlayerArguments.TryParse(
                Args("--rosettadds-perf", "--rosettadds-scenario", "x",
                     "--rosettadds-topic", "/t", "--rosettadds-qos", "reliable",
                     "--rosettadds-payload-bytes", "32", "--rosettadds-messages", "1",
                     "--rosettadds-ready-file", "/r", "--rosettadds-done-file", "/d",
                     "--rosettadds-metrics-file", "/m",
                     "--rosettadds-direction", "unity_to_ros2",
                     "--rosettadds-localhost-only", "true"),
                out var parsed, out _).Should().BeTrue();
            parsed.LocalhostOnly.Should().BeTrue();
        }
    }
}
```

- [ ] **Step 4.3: EditMode テスト assembly を確認 / 必要なら新規作成**

`Ros2Unity/Assets/Tests/EditMode/` 配下に既存 assembly があるか確認 (`*.asmdef`)。無ければ `ROSettaDDS.UnityEditMode.Tests` 等の名前で `.asmdef` を作る (platform: Editor のみ、includePlatforms: Editor、references に `ROSettaDDS.UnityPerfHarness` と `Unity.PerformanceTesting` / `UnityEngine.TestRunner` / `UnityEditor.TestRunner` を入れる)。既存 assembly があればそれをそのまま使う。

> 既存に EditMode 用 .asmdef が無い場合は新規作成する。AGENTS.md の Unity メタファイル運用に従い、`.asmdef` の `.meta` を必ず含める。

- [ ] **Step 4.4: テスト失敗確認 (compile error)**

```bash
uloop execute-dynamic-code --project-path Ros2Unity \
  --code 'UnityEditor.TestTools.TestRunner.Api.Scripting.Implementations.EditorTestPlayerLauncher.Run();'
```

または (上記が動かない場合) `scripts/unity/run_editmode.sh --filter "ROSettaDDS.UnityEditMode.Tests"`。

期待: `parsed.LocalhostOnly` 未定義で compile error → test run が setup error で FAIL。

- [ ] **Step 4.5: 実装**

`Ros2Unity/Assets/Perf/PerfPlayerArguments.cs` の `Parsed` struct に `public bool LocalhostOnly { get; init; } = true;` を追加。`TryParse` 内、`ProfilingMode` を読み終わった直後 (現行 135-165 行近辺) で:

```csharp
            bool localhostOnly = true;
            if (values.TryGetValue("--rosettadds-localhost-only", out string? lh))
            {
                if (!bool.TryParse(lh, out localhostOnly))
                {
                    error = "--rosettadds-localhost-only must be true or false";
                    return false;
                }
            }
```

を追加し、return 直前の `Parsed` 初期化に `LocalhostOnly = localhostOnly` を追加。`Parsed` には `init` setter が必要なので、struct か `record` かに合わせて `init` を許可する (現状の `Parsed` 定義に合わせる)。

- [ ] **Step 4.6: テスト pass 確認**

`scripts/unity/run_editmode.sh` (または同等の EditMode 実行) で 3 件 PASS を確認。

- [ ] **Step 4.7: コミット**

```bash
git add Ros2Unity/Assets/Perf/PerfPlayerArguments.cs \
        Ros2Unity/Assets/Tests/EditMode/PerfPlayerArgumentsTests.cs \
        Ros2Unity/Assets/Tests/EditMode/ROSettaDDS.UnityEditMode.Tests.asmdef \
        Ros2Unity/Assets/Tests/EditMode/ROSettaDDS.UnityEditMode.Tests.asmdef.meta 2>/dev/null
git commit -m "feat(unity): PerfPlayerArguments に --rosettadds-localhost-only を追加"
```

`.meta` ファイルが無い場合は `add` を 1 行ずつ確認してからコミットする。

---

## Task 5: PerfPlayerEntry の LocalhostOnly ハードコードを置換

**Files:**
- Modify: `Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:235-245`

- [ ] **Step 5.1: CreateParticipant の signature 変更**

`Ros2Unity/Assets/Perf/PerfPlayerEntry.cs:235-245` の `CreateParticipant` を以下の signature に変更:

```csharp
        private static DomainParticipant CreateParticipant(
            PerfPlayerArguments args,
            string entityName)
            => new DomainParticipant(new DomainParticipantOptions
            {
                DomainId = args.DomainId,
                ParticipantId = 0,
                EntityName = "rosettadds_perf_" + entityName,
                LocalhostOnly = args.LocalhostOnly,
                SpdpInterval = TimeSpan.FromMilliseconds(100),
                SedpInterval = TimeSpan.FromMilliseconds(100),
                UserWriterHeartbeatPeriod = TimeSpan.FromMilliseconds(100),
            });
```

- [ ] **Step 5.2: 呼び出し側を 2 箇所更新**

`RunUnityToRos2` (L86) と `RunRos2ToUnity` (L128) の `CreateParticipant(args.DomainId, ...)` を `CreateParticipant(args, ...)` に置換する。

- [ ] **Step 5.3: ビルド確認**

`scripts/unity/run_editmode.sh` で EditMode test が全 PASS することを確認 (既存 EditMode test の signature 変更による regression がないこと)。

- [ ] **Step 5.4: コミット**

```bash
git add Ros2Unity/Assets/Perf/PerfPlayerEntry.cs
git commit -m "refactor(unity): PerfPlayerEntry.CreateParticipant を args 受けに変更"
```

---

## Task 6: `applicationIdentifier.Android` を `com.ojii3.rosettadds.perf` に設定

**Files:**
- Modify: `Ros2Unity/ProjectSettings/ProjectSettings.asset`

- [ ] **Step 6.1: ProjectSettings.asset の該当行を編集**

`Ros2Unity/ProjectSettings/ProjectSettings.asset` 内の:

```yaml
    Android: com.UnityTechnologies.com.unity.template.urpblank
```

を以下に置換:

```yaml
    Android: com.ojii3.rosettadds.perf
```

> 重要: ProjectSettings.asset は YAML 形式。インデント (半角スペース 2 段) を崩さないこと。AGENTS.md の Unity メタファイル運用に従い、`.meta` は変更不要 (ProjectSettings.asset の meta は変更しない)。

- [ ] **Step 6.2: コミット**

```bash
git add Ros2Unity/ProjectSettings/ProjectSettings.asset
git commit -m "chore(unity): applicationIdentifier.Android を com.ojii3.rosettadds.perf に設定"
```

---

## Task 7: ROSettaDDSPerfPlayerBuilder に Android 経路を追加

**Files:**
- Modify: `Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs`

> 注意: Editor 側コードは TDD が難しい (Unity Editor 内部 API 依存) ので、本 Task は手動動作確認 (実機ビルド) で検証する。後段 Task 15 でエンドツーエンド確認。

- [ ] **Step 7.1: ParseBuildTarget を拡張**

`ParseBuildTarget` を以下に置換:

```csharp
        private static BuildTarget ParseBuildTarget(string value)
        {
            if (value == "StandaloneLinux64")
            {
                return BuildTarget.StandaloneLinux64;
            }
            if (value == "StandaloneOSX")
            {
                return BuildTarget.StandaloneOSX;
            }
            if (value == "Android")
            {
                return BuildTarget.Android;
            }
            throw new ArgumentException(
                "--rosettadds-perf-build-target must be StandaloneLinux64, StandaloneOSX, or Android");
        }
```

- [ ] **Step 7.2: BuildPlayer で NamedBuildTarget を切替 + 値保存/復元**

`BuildPlayer` (L37-) を以下に置換 (要 `UnityEditor.Build.NamedBuildTarget` の import 追加):

```csharp
        public static void BuildPlayer(string buildPath, string targetText, string backendText)
        {
            BuildTarget target = ParseBuildTarget(targetText);
            ScriptingImplementation backend = ParseBackend(backendText);
            NamedBuildTarget namedTarget = target == BuildTarget.Android
                ? NamedBuildTarget.Android
                : NamedBuildTarget.Standalone;
            ScriptingImplementation originalBackend =
                PlayerSettings.GetScriptingBackend(namedTarget);
            string originalApplicationIdentifier =
                PlayerSettings.GetApplicationIdentifier(namedTarget);
            Dictionary<string, string> preservedFiles = SnapshotProjectFiles();

            try
            {
                PlayerSettings.SetScriptingBackend(namedTarget, backend);
                if (target == BuildTarget.Android)
                {
                    PlayerSettings.SetApplicationIdentifier(
                        namedTarget, "com.ojii3.rosettadds.perf");
                }

                string directory = Path.GetDirectoryName(buildPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new BuildPlayerOptions
                {
                    scenes = EnabledScenes(),
                    target = target,
                    targetGroup = target == BuildTarget.Android
                        ? BuildTargetGroup.Android
                        : BuildTargetGroup.Standalone,
                    locationPathName = buildPath,
                    options = BuildOptions.Development,
                };

                BuildReportOrThrow(options);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(namedTarget, originalBackend);
                if (target == BuildTarget.Android)
                {
                    PlayerSettings.SetApplicationIdentifier(namedTarget, originalApplicationIdentifier);
                }
                RestoreProjectFiles(preservedFiles);
            }
        }
```

> 実装メモ: Unity 6 の `BuildPlayerOptions.targetGroup` は deprecated だがまだ動く。`target` だけでも `targetGroup` は internal で補完される。明示しておくほうが portable。

- [ ] **Step 7.3: ビルド sanity 確認 (任意、Editor が起動可能な場合のみ)**

`uloop execute-dynamic-code --project-path Ros2Unity --code 'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("/tmp/rosettadds-perf-sanity", "StandaloneLinux64", "il2cpp"); return "ok";'` を実行し、`StandaloneLinux64` の既存 desktop build が壊れていないことを確認。失敗時は `systematic-debugging` スキルで切り分け、Step 7.2 の `targetGroup` 設定周りを疑う。

- [ ] **Step 7.4: コミット**

```bash
git add Ros2Unity/Assets/Editor/ROSettaDDSPerfPlayerBuilder.cs
git commit -m "feat(editor): ROSettaDDSPerfPlayerBuilder に Android + IL2CPP 経路を追加"
```

---

## Task 8: `IProcessDriver` interface と `LaunchSpec` record を新設

**Files:**
- Create: `tools/rosettadds-perf-runner/IProcessDriver.cs`
- Create: `tools/rosettadds-perf-runner/LaunchSpec.cs`

- [ ] **Step 8.1: LaunchSpec.cs を作成**

`tools/rosettadds-perf-runner/LaunchSpec.cs` を新規作成:

```csharp
namespace ROSettaDDS.PerfRunner;

internal enum LogKind
{
    Stdout,
    Stderr,
}

internal sealed record LaunchSpec(
    string Kind,                 // "player" / "helper"
    string ScenarioName,
    string Direction,            // "unity_to_ros2" / "ros2_to_unity"
    int DomainId,
    string Topic,
    string Qos,                  // "reliable" / "best_effort"
    int PayloadBytes,
    int Messages,
    bool LocalhostOnly,
    string ReadyFile,
    string DoneFile,
    string? ReleaseFile,         // null 許容
    string MetricsFile,
    string PlayerExecutable,     // desktop 用
    string? ApkFile,             // android 用
    string? DevicePersistentDir, // android 用 (e.g. /sdcard/Android/data/<pkg>/files/rosettadds-perf)
    int HelperMeasureStart,      // helper 起動時に --measure-start を渡すか (0/1)
    string HelperMode,           // "pub" / "sub"
    string HelperTopic,
    IReadOnlyList<string> ExtraArgs);
```

- [ ] **Step 8.2: IProcessDriver.cs を作成**

`tools/rosettadds-perf-runner/IProcessDriver.cs` を新規作成:

```csharp
namespace ROSettaDDS.PerfRunner;

internal interface IProcessDriver : IDisposable
{
    Task StartAsync(LaunchSpec spec, CancellationToken ct);
    Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct);
    Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
    void Kill();
    Stream OpenLogAsync(LogKind kind, CancellationToken ct);
    Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct);
}
```

- [ ] **Step 8.3: ビルド確認**

```bash
dotnet build tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj --nologo
```

期待: 0 warning、0 error (誰も実装していなくても interface と record 単体でビルドは通る)。

- [ ] **Step 8.4: コミット**

```bash
git add tools/rosettadds-perf-runner/IProcessDriver.cs tools/rosettadds-perf-runner/LaunchSpec.cs
git commit -m "feat(runner): IProcessDriver interface と LaunchSpec record を新設"
```

---

## Task 9: `AdbClient` 抽象と adb コマンドライン組み立て (TDD)

**Files:**
- Create: `tools/rosettadds-perf-runner/AdbClient.cs`
- Create: `tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs`
- Create: `tools/rosettadds-perf-runner.Tests/AdbClientTests.cs`

- [ ] **Step 9.1: 失敗するテストを追加**

`tools/rosettadds-perf-runner.Tests/AdbClientTests.cs` を新規作成し、以下を記述する:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class AdbClientTests
{
    [Fact]
    public async Task InstallApk_は_adb_install_r_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.InstallApkAsync("/tmp/x.apk", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().StartWith("adb -s ABC install -r /tmp/x.apk");
    }

    [Fact]
    public async Task ForceStop_は_adb_shell_am_force_stop_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.ForceStopAsync("com.ojii3.rosettadds.perf", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().Be("adb -s ABC shell am force-stop com.ojii3.rosettadds.perf");
    }

    [Fact]
    public async Task StartActivity_は_全引数を_args_extra_に_連結する()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.StartActivityAsync(
            "com.ojii3.rosettadds.perf",
            "com.unity3d.player.GameActivity",
            new[] { "--rosettadds-topic", "/t", "--rosettadds-domain-id", "42" },
            CancellationToken.None);

        fake.Calls.Should().ContainSingle();
        string call = fake.Calls[0];
        call.Should().StartWith("adb -s ABC shell am start -W -n com.ojii3.rosettadds.perf/com.unity3d.player.GameActivity");
        call.Should().Contain("--es args \"--rosettadds-topic /t --rosettadds-domain-id 42\"");
    }

    [Fact]
    public async Task PullFile_は_adb_pull_を_組み立てる()
    {
        var fake = new FakeAdbClient();
        var client = new AdbClient(fake, serial: "ABC");
        await client.PullFileAsync("/sdcard/x", "/tmp/x", CancellationToken.None);

        fake.Calls.Should().ContainSingle()
            .Which.Should().Be("adb -s ABC pull /sdcard/x /tmp/x");
    }
}
```

- [ ] **Step 9.2: FakeAdbClient スタブを追加**

`tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs` を新規作成:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeAdbClient : IAdbCommandSink
{
    public List<string> Calls { get; } = new();

    public Task<int> RunAsync(string command, CancellationToken ct)
    {
        Calls.Add(command);
        return Task.FromResult(0);
    }
}
```

- [ ] **Step 9.3: テスト失敗確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~AdbClientTests" --nologo
```

期待: 4 件 compile error (`AdbClient` / `IAdbCommandSink` 未定義) で FAIL。

- [ ] **Step 9.4: AdbClient 実装**

`tools/rosettadds-perf-runner/AdbClient.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal interface IAdbCommandSink
{
    Task<int> RunAsync(string command, CancellationToken ct);
}

internal sealed class AdbClient : IAdbCommandSink
{
    private readonly IAdbCommandSink _sink;
    private readonly string _serial;

    public AdbClient(IAdbCommandSink sink, string serial)
    {
        _sink = sink;
        _serial = serial;
    }

    public Task<int> RunAsync(string command, CancellationToken ct)
        => _sink.RunAsync(command, ct);

    public Task InstallApkAsync(string apkPath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} install -r {apkPath}", ct);

    public Task ForceStopAsync(string packageId, CancellationToken ct)
        => RunAsync($"adb -s {_serial} shell am force-stop {packageId}", ct);

    public Task StartActivityAsync(
        string packageId,
        string activityComponent,
        IReadOnlyList<string> playerArgs,
        CancellationToken ct)
    {
        string joined = string.Join(" ", playerArgs);
        return RunAsync(
            $"adb -s {_serial} shell am start -W -n {packageId}/{activityComponent} " +
            $"--es args \"{joined}\"",
            ct);
    }

    public Task PullFileAsync(string remotePath, string localPath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} pull {remotePath} {localPath}", ct);

    public Task<string> RunWithStdoutAsync(string command, CancellationToken ct)
    {
        // 実 subprocess 用の薄いラッパ。FakeAdbClient は未実装なのでテスト時は使わない。
        var psi = new ProcessStartInfo("/bin/sh", "-c " + Escape(command))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return Task.FromResult(stdout);
    }

    private static string Escape(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

internal sealed class RealAdbCommandSink : IAdbCommandSink
{
    public Task<int> RunAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-c " + Escape(command))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var p = Process.Start(psi)!;
        p.WaitForExit();
        return Task.FromResult(p.ExitCode);
    }

    private static string Escape(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
```

> 実装メモ: shell 経由で adb を起動しているのは、command を文字列で組み立てるテストの assert 容易性を上げるため。`ProcessStartInfo.ArgumentList` 経由にすると argv 配列を扱う必要があり、テストコードの assert が冗長になる。

- [ ] **Step 9.5: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~AdbClientTests" --nologo
```

期待: 4 件すべて PASS。

- [ ] **Step 9.6: コミット**

```bash
git add tools/rosettadds-perf-runner/AdbClient.cs \
        tools/rosettadds-perf-runner.Tests/AdbClientTests.cs \
        tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs
git commit -m "feat(runner): AdbClient 抽象と adb コマンドライン組み立て"
```

---

## Task 10: `AndroidAdbDriver` の実装 (TDD)

**Files:**
- Create: `tools/rosettadds-perf-runner/AndroidAdbDriver.cs`
- Create: `tools/rosettadds-perf-runner.Tests/AndroidAdbDriverTests.cs`

- [ ] **Step 10.1: 失敗するテストを追加**

`tools/rosettadds-perf-runner.Tests/AndroidAdbDriverTests.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using ROSettaDDS.PerfRunner.Tests.Fakes;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class AndroidAdbDriverTests : IDisposable
{
    private readonly string _tmp;
    private readonly FakeAdbClient _fake;
    private readonly AdbClient _client;
    private readonly AndroidAdbDriver _driver;

    public AndroidAdbDriverTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "rosettadds-android-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _fake = new FakeAdbClient();
        _client = new AdbClient(_fake, serial: "ABC");
        _driver = new AndroidAdbDriver(
            adb: _client,
            packageId: "com.ojii3.rosettadds.perf",
            activityComponent: "com.unity3d.player.GameActivity",
            devicePersistentDir: "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf",
            localArtifactDir: _tmp);
    }

    public void Dispose()
    {
        _driver.Dispose();
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static LaunchSpec Spec() => new(
        Kind: "player",
        ScenarioName: "x",
        Direction: "unity_to_ros2",
        DomainId: 42,
        Topic: "/t",
        Qos: "reliable",
        PayloadBytes: 32,
        Messages: 100,
        LocalhostOnly: false,
        ReadyFile: "ready",
        DoneFile: "done",
        ReleaseFile: null,
        MetricsFile: "metrics.ndjson",
        PlayerExecutable: "/nonexistent",
        ApkFile: "/tmp/x.apk",
        DevicePersistentDir: "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf",
        HelperMeasureStart: 0,
        HelperMode: "sub",
        HelperTopic: "/t",
        ExtraArgs: new[] { "--rosettadds-topic", "/t", "--rosettadds-localhost-only", "false" });

    [Fact]
    public async Task StartAsync_は_install_force_stop_am_start_の_順に_呼ぶ()
    {
        await _driver.StartAsync(Spec(), CancellationToken.None);

        _fake.Calls.Should().HaveCount(3);
        _fake.Calls[0].Should().StartWith("adb -s ABC install -r /tmp/x.apk");
        _fake.Calls[1].Should().Be("adb -s ABC shell am force-stop com.ojii3.rosettadds.perf");
        _fake.Calls[2].Should().StartWith("adb -s ABC shell am start -W -n com.ojii3.rosettadds.perf/com.unity3d.player.GameActivity");
        _fake.Calls[2].Should().Contain("--es args");
    }

    [Fact]
    public async Task StartAsync_install_失敗で_例外()
    {
        _fake.ExitCodeOverride = 1;
        _fake.StderrOverride = "Failure [INSTALL_FAILED]";
        var act = () => _driver.StartAsync(Spec(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*install*Failure*");
    }

    [Fact]
    public async Task WaitForSentinelAsync_1回目失敗_2回目成功で_true()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 1, 0 });
        _fake.FileProvider = (remote, local) =>
        {
            File.WriteAllText(local, "ok");
        };
        bool ok = await _driver.WaitForSentinelAsync("ready", TimeSpan.FromSeconds(2), CancellationToken.None);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForSentinelAsync_全失敗で_false()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });
        bool ok = await _driver.WaitForSentinelAsync("ready", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForSentinelAsync_pull_以外_の_exit_code_で_IOException()
    {
        _fake.ScriptedExitCodes = new Queue<int>(new[] { 137 });
        var act = () => _driver.WaitForSentinelAsync("ready", TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();
    }
}
```

> 上記テストが要求する Fake の機能 (`ExitCodeOverride` / `StderrOverride` / `ScriptedExitCodes` / `FileProvider`) は Step 10.2 で `FakeAdbClient` を新規作成して提供する。

- [ ] **Step 10.2: FakeAdbClient を `AdbResult` 対応版で作成**

`tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeAdbClient : IAdbCommandSink
{
    public List<string> Calls { get; } = new();
    public int ExitCodeOverride { get; set; } = 0;
    public string StderrOverride { get; set; } = string.Empty;
    public Queue<int>? ScriptedExitCodes { get; set; }
    public Action<string, string>? FileProvider { get; set; }

    public Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        Calls.Add(command);
        int code;
        if (ScriptedExitCodes is { } q && q.Count > 0)
        {
            code = q.Dequeue();
        }
        else
        {
            code = ExitCodeOverride;
        }
        if (command.Contains("pull ", StringComparison.Ordinal) && code == 0 && FileProvider is { } fp)
        {
            string[] parts = command.Split("pull ", 2)[1].Split(' ', 2);
            fp(parts[0], parts[1]);
        }
        return Task.FromResult(new AdbResult(code, string.Empty, StderrOverride));
    }
}
```

- [ ] **Step 10.3: AdbClient 実装 (`AdbResult` 戻り値版)**

`tools/rosettadds-perf-runner/AdbClient.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal readonly record struct AdbResult(int ExitCode, string Stdout, string Stderr);

internal interface IAdbCommandSink
{
    Task<AdbResult> RunAsync(string command, CancellationToken ct);
}

internal sealed class AdbClient : IAdbCommandSink
{
    private readonly IAdbCommandSink _sink;
    private readonly string _serial;

    public AdbClient(IAdbCommandSink sink, string serial)
    {
        _sink = sink;
        _serial = serial;
    }

    public Task<AdbResult> RunAsync(string command, CancellationToken ct)
        => _sink.RunAsync(command, ct);

    public async Task<AdbResult> InstallApkAsync(string apkPath, CancellationToken ct)
    {
        var r = await RunAsync($"adb -s {_serial} install -r {apkPath}", ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"adb install failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
        return r;
    }

    public Task<AdbResult> ForceStopAsync(string packageId, CancellationToken ct)
        => RunAsync($"adb -s {_serial} shell am force-stop {packageId}", ct);

    public Task<AdbResult> StartActivityAsync(
        string packageId,
        string activityComponent,
        IReadOnlyList<string> playerArgs,
        CancellationToken ct)
    {
        string joined = string.Join(" ", playerArgs);
        return RunAsync(
            $"adb -s {_serial} shell am start -W -n {packageId}/{activityComponent} " +
            $"--es args \"{joined}\"",
            ct);
    }

    public Task<AdbResult> PullFileAsync(string remotePath, string localPath, CancellationToken ct)
        => RunAsync($"adb -s {_serial} pull {remotePath} {localPath}", ct);
}

internal sealed class RealAdbCommandSink : IAdbCommandSink
{
    public async Task<AdbResult> RunAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-c " + Escape(command))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new AdbResult(p.ExitCode, stdout, stderr);
    }

    private static string Escape(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
```

- [ ] **Step 10.4: テスト失敗確認 (compile error)**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~AndroidAdbDriverTests" --nologo
```

期待: 5 件 compile error (`AndroidAdbDriver` 未定義) で FAIL。

- [ ] **Step 10.5: AndroidAdbDriver 実装**

`tools/rosettadds-perf-runner/AndroidAdbDriver.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal sealed class AndroidAdbDriver : IProcessDriver
{
    private readonly AdbClient _adb;
    private readonly string _packageId;
    private readonly string _activityComponent;
    private readonly string _devicePersistentDir;
    private readonly string _localArtifactDir;
    private Process? _logcat;
    private bool _disposed;

    public AndroidAdbDriver(
        AdbClient adb,
        string packageId,
        string activityComponent,
        string devicePersistentDir,
        string localArtifactDir)
    {
        _adb = adb;
        _packageId = packageId;
        _activityComponent = activityComponent;
        _devicePersistentDir = devicePersistentDir;
        _localArtifactDir = localArtifactDir;
    }

    public async Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        if (spec.ApkFile is null)
        {
            throw new InvalidOperationException("AndroidAdbDriver requires spec.ApkFile");
        }
        await _adb.InstallApkAsync(spec.ApkFile, ct);
        await _adb.ForceStopAsync(_packageId, ct);
        var args = new List<string>(spec.ExtraArgs);
        await _adb.StartActivityAsync(_packageId, _activityComponent, args, ct);
    }

    public async Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        string remote = _devicePersistentDir + "/" + name;
        string local = Path.Combine(_localArtifactDir, name);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _adb.PullFileAsync(remote, local, ct);
            if (r.ExitCode == 0)
            {
                return true;
            }
            if (r.ExitCode != 1)
            {
                throw new IOException(
                    $"adb pull failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
            }
            await Task.Delay(100, ct);
        }
        return false;
    }

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Player 側は am force-stop するまで生きている。シナリオ終了時は Kill() を
        // runner 側が呼ぶので、本メソッドは通常呼ばれない。念のため短い polling で
        // 0 を返す。
        return Task.FromResult(0);
    }

    public void Kill()
    {
        // fire-and-forget なので、戻り値は無視。
        _ = _adb.ForceStopAsync(_packageId, CancellationToken.None);
    }

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
    {
        // 簡略化: logcat タグ絞り込み済みの stdout を subprocess 起動して stream 化。
        // AndroidAdbDriver は logcat 制御を持たず、Program 側で logcat を別途起動する
        // 経路のほうがテスタビリティが高いため、本メソッドは NotSupported。
        throw new NotSupportedException(
            "AndroidAdbDriver.OpenLogAsync: logcat streaming is owned by Program.RunScenario, not this driver");
    }

    public async Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        string remote = _devicePersistentDir + "/" + remoteName;
        var r = await _adb.PullFileAsync(remote, localPath, ct);
        if (r.ExitCode != 0)
        {
            throw new IOException(
                $"adb pull failed (exit={r.ExitCode}): {r.Stderr.Trim()}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_logcat is { } p && !p.HasExited)
        {
            try { p.Kill(); } catch { }
        }
        _logcat?.Dispose();
    }
}
```

> 設計メモ: `OpenLogAsync` を `AndroidAdbDriver` に持たせる案は spec で書いたが、`logcat` は player ライフサイクルとは独立した tail なので、Program 側で別 subprocess として扱うほうが扱いやすい。spec を「Program 側が `adb logcat` を直接 spawn する」形に明確化し直す。spec への修正は本 Task の最終ステップで 1 行だけ加筆する。

- [ ] **Step 10.6: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~AndroidAdbDriverTests" --nologo
```

期待: 5 件すべて PASS。

- [ ] **Step 10.7: spec に 1 行追記 (logcat 経路の明確化)**

`docs/superpowers/specs/2026-06-26-android-il2cpp-perf-runner-design.md` の `OpenLogAsync` 説明箇所を以下に置換:

> - `OpenLogAsync`: `IProcessDriver` interface 上は存在するが、`AndroidAdbDriver` 実装は `NotSupportedException` を投げる。Android 経路の logcat tail は Program.RunScenario 側で `adb logcat -T <iso> -s Unity:* PlayerActivity:* Debug:*` を別 subprocess として起動し、`Stream.CopyToAsync` で `player.stdout.log` / `player.stderr.log` に書き出す。理由: logcat は player ライフサイクル (start/exit) とは独立した tail であり、driver 抽象に含めるとテスト容易性が落ちるため。

- [ ] **Step 10.8: コミット (2 件)**

```bash
git add tools/rosettadds-perf-runner/AndroidAdbDriver.cs \
        tools/rosettadds-perf-runner.Tests/AndroidAdbDriverTests.cs \
        tools/rosettadds-perf-runner.Tests/Fakes/FakeAdbClient.cs \
        tools/rosettadds-perf-runner/AdbClient.cs \
        tools/rosettadds-perf-runner.Tests/AdbClientTests.cs
git commit -m "feat(runner): AndroidAdbDriver 実装 (AdbClient 経由)"
```

```bash
git add docs/superpowers/specs/2026-06-26-android-il2cpp-perf-runner-design.md
git commit -m "docs(spec): logcat tail は Program 側管理であることを明文化"
```

---

## Task 11: `FakeProcessDriver` (テスト用 fake) を新設

**Files:**
- Create: `tools/rosettadds-perf-runner.Tests/Fakes/FakeProcessDriver.cs`

- [ ] **Step 11.1: FakeProcessDriver を作成**

`tools/rosettadds-perf-runner.Tests/Fakes/FakeProcessDriver.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.PerfRunner;

namespace ROSettaDDS.PerfRunner.Tests.Fakes;

internal sealed class FakeProcessDriver : IProcessDriver
{
    public List<LaunchSpec> StartCalls { get; } = new();
    public List<string> WaitForSentinelCalls { get; } = new();
    public List<TimeSpan> WaitForExitCalls { get; } = new();
    public List<(string Remote, string Local)> CopyFileCalls { get; } = new();
    public int KillCalls { get; private set; }

    public Func<LaunchSpec, CancellationToken, Task>? StartImpl { get; set; }
    public Func<string, TimeSpan, CancellationToken, Task<bool>>? WaitForSentinelImpl { get; set; }
    public Func<TimeSpan, CancellationToken, Task<int>>? WaitForExitImpl { get; set; }
    public Action? KillImpl { get; set; }
    public Func<LogKind, CancellationToken, Stream>? OpenLogImpl { get; set; }
    public Func<string, string, CancellationToken, Task>? CopyFileImpl { get; set; }

    public Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        StartCalls.Add(spec);
        return StartImpl?.Invoke(spec, ct) ?? Task.CompletedTask;
    }

    public Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        WaitForSentinelCalls.Add(name);
        return WaitForSentinelImpl?.Invoke(name, timeout, ct) ?? Task.FromResult(true);
    }

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        WaitForExitCalls.Add(timeout);
        return WaitForExitImpl?.Invoke(timeout, ct) ?? Task.FromResult(0);
    }

    public void Kill()
    {
        KillCalls++;
        KillImpl?.Invoke();
    }

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
        => OpenLogImpl?.Invoke(kind, ct) ?? new MemoryStream();

    public Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        CopyFileCalls.Add((remoteName, localPath));
        return CopyFileImpl?.Invoke(remoteName, localPath, ct) ?? Task.CompletedTask;
    }

    public void Dispose() { }
}
```

- [ ] **Step 11.2: ビルド確認**

```bash
dotnet build tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --nologo
```

期待: 0 warning、0 error。

- [ ] **Step 11.3: コミット**

```bash
git add tools/rosettadds-perf-runner.Tests/Fakes/FakeProcessDriver.cs
git commit -m "test(runner): FakeProcessDriver を追加"
```

---

## Task 12: `DesktopProcessDriver` を新設 (既存 `ProcessCapture` の薄いラッパ)

**Files:**
- Create: `tools/rosettadds-perf-runner/DesktopProcessDriver.cs`

- [ ] **Step 12.1: DesktopProcessDriver を作成**

`tools/rosettadds-perf-runner/DesktopProcessDriver.cs` を新規作成:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ROSettaDDS.PerfRunner;

internal sealed class DesktopProcessDriver : IProcessDriver
{
    private ProcessCapture? _capture;
    private string _sentinelDir = string.Empty;
    private bool _disposed;

    public Task StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        // 既存の StartPlayer / StartHelper のロジックをここに移植するのは別 PR。
        // 本 Task では「IProcessDriver を満たす空実装」を提供し、Program 側の
        // IProcessDriver 利用を enable する。StartImpl 経由で Program 側から実
        // ProcessCapture を生成・保持する設計は Task 13 で導入。
        throw new NotImplementedException(
            "DesktopProcessDriver is not wired into Program.RunScenario yet; " +
            "see Task 13.");
    }

    public Task<bool> WaitForSentinelAsync(string name, TimeSpan timeout, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_capture is null) return Task.FromResult(-1);
        return _capture.WaitForExitAsync(timeout);
    }

    public void Kill() => _capture?.Kill();

    public Stream OpenLogAsync(LogKind kind, CancellationToken ct)
        => throw new NotImplementedException();

    public Task CopyFileFromAsync(string remoteName, string localPath, CancellationToken ct)
    {
        // desktop では sentinel ファイルが既に local 側にあるので、no-op。
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture?.Dispose();
    }
}
```

> 注: Task 12 は「`IProcessDriver` 抽象を `DesktopProcessDriver` 型として提供して compile error を消しつつ、Program 側の大規模 refactor は Task 13 に回す」段階的な導入。Task 13 で `Program.RunScenario` が `IProcessDriver` 経由で動くように全面 refactor する。

- [ ] **Step 12.2: ビルド確認**

```bash
dotnet build tools/rosettadds-perf-runner/rosettadds-perf-runner.csproj --nologo
```

期待: 0 warning、0 error。

- [ ] **Step 12.3: コミット**

```bash
git add tools/rosettadds-perf-runner/DesktopProcessDriver.cs
git commit -m "feat(runner): DesktopProcessDriver の stub を提供 (本実装は Task 13)"
```

---

## Task 13: `Program.RunScenario` を `IProcessDriver` 経由に refactor (TDD)

**Files:**
- Modify: `tools/rosettadds-perf-runner/Program.cs`
- Create: `tools/rosettadds-perf-runner.Tests/ProgramTests.cs`

> 大規模 refactor。先に ProgramTests で期待挙動を pin してから実装する。

- [ ] **Step 13.1: 失敗するテストを追加**

`tools/rosettadds-perf-runner.Tests/ProgramTests.cs` を新規作成:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using ROSettaDDS.PerfRunner;
using Xunit;

namespace ROSettaDDS.PerfRunner.Tests;

public class ProgramTests
{
    [Fact]
    public void BuildHelperEnv_Android_build_target_で_RosLocalhostOnly_が_0()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        var env = new Dictionary<string, string?>();
        Program.BuildHelperEnv(options, domainId: 42, env);

        env.Should().ContainKey("ROS_LOCALHOST_ONLY");
        env["ROS_LOCALHOST_ONLY"].Should().Be("0");
        env.Should().ContainKey("RMW_IMPLEMENTATION").WhoseValue.Should().Be("rmw_fastrtps_cpp");
        env.Should().ContainKey("ROS_DOMAIN_ID").WhoseValue.Should().Be("42");
    }

    [Fact]
    public void BuildHelperEnv_StandaloneLinux64_build_target_で_RosLocalhostOnly_が_1()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        var env = new Dictionary<string, string?>();
        Program.BuildHelperEnv(options, domainId: 1, env);

        env["ROS_LOCALHOST_ONLY"].Should().Be("1");
    }

    [Fact]
    public void CreatePlayerDriver_Android_build_target_で_AndroidAdbDriver_を返す()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        using var driver = Program.CreatePlayerDriver(options, artifactDir: "/tmp/x", apkFile: "/tmp/x.apk");
        driver.Should().BeOfType<AndroidAdbDriver>();
    }

    [Fact]
    public void CreatePlayerDriver_StandaloneLinux64_build_target_で_DesktopProcessDriver_を返す()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        using var driver = Program.CreatePlayerDriver(options, artifactDir: "/tmp/x", apkFile: null);
        driver.Should().BeOfType<DesktopProcessDriver>();
    }

    [Fact]
    public void CreateHelperDriver_は_desktop_と_android_両方で_DesktopProcessDriver_を返す()
    {
        // helper は host 側で動くので、両 build target で同じ DesktopProcessDriver
        // を使う (ProcessCapture で起動する)。Android 経路で変わるのは env 注入と
        // driver 起動引数のみ。
        var android = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        Program.CreateHelperDriver(android).Should().BeOfType<DesktopProcessDriver>();

        var linux = RunnerOptions.Parse(new[] { "--build-target", "StandaloneLinux64" });
        Program.CreateHelperDriver(linux).Should().BeOfType<DesktopProcessDriver>();
    }
}
```

- [ ] **Step 13.2: テスト失敗確認 (compile error)**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~ProgramTests" --nologo
```

期待: 5 件 compile error (`Program.BuildHelperEnv` / `Program.CreatePlayerDriver` / `Program.CreateHelperDriver` 未定義) で FAIL。

- [ ] **Step 13.3: `Program.cs` に `internal static` ヘルパーを追加**

`tools/rosettadds-perf-runner/Program.cs` の末尾に以下を追加:

```csharp
namespace ROSettaDDS.PerfRunner;

internal static partial class ProgramHelpers
{
    internal static void BuildHelperEnv(RunnerOptions options, int domainId, IDictionary<string, string?> env)
    {
        env["ROS_LOCALHOST_ONLY"] = options.BuildTarget == "Android" ? "0" : "1";
        env["RMW_IMPLEMENTATION"] = "rmw_fastrtps_cpp";
        env["ROS_DOMAIN_ID"] = domainId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IProcessDriver CreatePlayerDriver(
        RunnerOptions options,
        string artifactDir,
        string? apkFile)
    {
        if (options.BuildTarget == "Android")
        {
            string deviceSerial = options.AndroidDevice
                ?? throw new System.InvalidOperationException(
                    "--android-device is required (no auto-detect implemented yet)");
            var adb = new AdbClient(new RealAdbCommandSink(), deviceSerial);
            string devicePersistentDir =
                $"/sdcard/Android/data/{options.AndroidPackage}/files/rosettadds-perf";
            return new AndroidAdbDriver(
                adb: adb,
                packageId: options.AndroidPackage,
                activityComponent: options.AndroidActivity,
                devicePersistentDir: devicePersistentDir,
                localArtifactDir: artifactDir);
        }
        return new DesktopProcessDriver();
    }

    internal static IProcessDriver CreateHelperDriver(RunnerOptions options)
    {
        // helper は host 側で動くため、Android / desktop どちらでも DesktopProcessDriver。
        return new DesktopProcessDriver();
    }
}

internal static class Program
{
    public static void BuildHelperEnv(RunnerOptions options, int domainId, IDictionary<string, string?> env)
        => ProgramHelpers.BuildHelperEnv(options, domainId, env);

    public static IProcessDriver CreatePlayerDriver(RunnerOptions options, string artifactDir, string? apkFile)
        => ProgramHelpers.CreatePlayerDriver(options, artifactDir, apkFile);

    public static IProcessDriver CreateHelperDriver(RunnerOptions options)
        => ProgramHelpers.CreateHelperDriver(options);
}
```

> 実装メモ: `Program` class のエントリポイント (`return MainAsync(args)`) は Program.cs 上部に存在し、ファイル末尾の追加は衝突しない。`partial class` 化はせず、Program.cs 上部の `static async Task<int> MainAsync` はそのまま残し、末尾に別 `internal static class Program` を追加する (C# では同名 class を 1 ファイル内に複数宣言できないので、namespace 内で別ファイルに分割するのが本来。本 plan では ProgramHelpers に処理を寄せ、Program への facade で公開する。最終形の配置は Task 13 完了時に整理する)。

- [ ] **Step 13.4: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~ProgramTests" --nologo
```

期待: 5 件すべて PASS。

- [ ] **Step 13.5: `RunScenario` の refactor (large diff)**

`tools/rosettadds-perf-runner/Program.cs` の `RunScenario` を `IProcessDriver` 経由に書き換える。差分の要約:

- `StartPlayer` 呼び出しを `ProgramHelpers.CreatePlayerDriver(options, scenarioDir, apkFile: null)` に置換 (desktop) / `apkFile: playerExecutable` (Android、APK 出力 path は `PerfRunnerPaths.PlayerExecutablePath` で `.apk` になる)。
- `StartHelper` 呼び出しは `ProgramHelpers.CreateHelperDriver(options)` に置換。
- `WaitForFile(readyFile, ...)` を `driver.WaitForSentinelAsync("ready", TimeSpan.FromSeconds(20), ct)` に置換。`WaitForFile` 関数は本 Task 内で `internal static async Task WaitForFile(...)` のまま残し、Android 経路では使われない。
- desktop 経路の `WaitForExitAsync` は `ProcessCapture.WaitForExitAsync` を直接呼ぶ (既存挙動維持)。Android 経路では `driver.WaitForExitAsync(TimeSpan.FromMinutes(...), ct)` を呼ぶ。
- `metrics.ndjson` / `profiler.raw` の回収: desktop では既存 `WaitForFile` ベース (sentinel polling)。Android では `driver.CopyFileFromAsync("metrics.ndjson", metricsFile, ct)` と `driver.CopyFileFromAsync("profiler.raw", profilerFile, ct)` で `artifacts/<runId>/<scenario>/` 配下に pull。
- logcat streaming: Android 経路では `RunScenario` 内で別途 `Process.Start("adb", "-s <serial> logcat -T <iso> -s Unity:* PlayerActivity:* Debug:*")` を起動し、`player.stdout.log` にリダイレクト。`RunScenario` 終了時に `process.Kill()` で停止。`logcat` subprocess は `IProcessDriver` には含めず、Program 側の helper として private 関数 `StartLogcatCapture(serial, logFile)` / `StopLogcatCapture()` に閉じる。
- helper env 注入: `env => { ProgramHelpers.BuildHelperEnv(options, domainId, env); }` に置換。

実装は subagent 駆動 (subagent-driven-development skill) で 1 step ずつ TDD で進める。本 plan では方針と API シグネチャのみ pin する。差分の最終形は PR description に貼る。

- [ ] **Step 13.6: 既存 desktop scenario の regression 確認**

```bash
scripts/ros2/build_helper.sh
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --nologo
dotnet test tests/rosettadds.Tests/rosettadds.Tests.csproj --nologo
```

期待: 既存 desktop test は無改修で全 PASS であること。

- [ ] **Step 13.7: コミット**

```bash
git add tools/rosettadds-perf-runner/Program.cs tools/rosettadds-perf-runner.Tests/ProgramTests.cs
git commit -m "feat(runner): Program.RunScenario を IProcessDriver 経由に refactor"
```

---

## Task 14: `IProcessDriver` 経由の Android scenario 統合テスト

**Files:**
- Modify: `tools/rosettadds-perf-runner.Tests/ProgramTests.cs`

- [ ] **Step 14.1: Android シナリオの統合テストを追加**

`ProgramTests.cs` に追加 (Task 13 で `CreatePlayerDriver` / `CreateHelperDriver` が公開済みであることを前提):

```csharp
    [Fact]
    public void Android_build_target_の_player_は_AndroidAdbDriver_helper_は_DesktopProcessDriver()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        using var player = Program.CreatePlayerDriver(options, "/tmp/x", "/tmp/x.apk");
        using var helper = Program.CreateHelperDriver(options);

        player.Should().BeOfType<AndroidAdbDriver>();
        helper.Should().BeOfType<DesktopProcessDriver>();
    }

    [Fact]
    public void Android_build_target_の_player_artifact_dir_が_PersistentDir_に_反映される()
    {
        var options = RunnerOptions.Parse(new[] { "--build-target", "Android" });
        using var driver = Program.CreatePlayerDriver(options, "/tmp/x", "/tmp/x.apk");
        var android = (AndroidAdbDriver)driver;
        // リフレクションで private フィールド _devicePersistentDir を読む
        var field = typeof(AndroidAdbDriver).GetField(
            "_devicePersistentDir",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.GetValue(android).Should().Be(
            "/sdcard/Android/data/com.ojii3.rosettadds.perf/files/rosettadds-perf");
    }
```

- [ ] **Step 14.2: テスト pass 確認**

```bash
dotnet test tools/rosettadds-perf-runner.Tests/rosettadds-perf-runner.Tests.csproj --filter "FullyQualifiedName~ProgramTests" --nologo
```

期待: 全 PASS。

- [ ] **Step 14.3: コミット**

```bash
git add tools/rosettadds-perf-runner.Tests/ProgramTests.cs
git commit -m "test(runner): Android scenario 統合テストを追加"
```

---

## Task 15: 全体回帰とエンドツーエンド動作確認 (実機)

**Files:**
- (変更なし、ビルド + 実機 smoke のみ)

- [ ] **Step 15.1: フルビルド**

```bash
dotnet build rosettadds.sln --nologo
```

期待: 0 warning、0 error。

- [ ] **Step 15.2: 全テスト実行**

```bash
dotnet test rosettadds.sln --nologo
```

期待: すべて PASS。

- [ ] **Step 15.3: EditMode テスト (Unity) 実行**

```bash
scripts/unity/run_editmode.sh
```

期待: 全 PASS。

- [ ] **Step 15.4: APK ビルド sanity (実機投入前)**

`uloop execute-dynamic-code --project-path Ros2Unity --code 'ROSettaDDS.EditorTools.ROSettaDDSPerfPlayerBuilder.BuildPlayer("/tmp/rosettadds-perf-debug.apk", "Android", "il2cpp"); return "ok";'` を実行し、APK が生成されることを確認。失敗時は `systematic-debugging` スキルで切り分け、Step 7.2 の `NamedBuildTarget.Android` 設定 / Unity Editor の Android Build Support / NDK インストール状況を疑う。

- [ ] **Step 15.5: 実機 smoke (Android 経路)**

端末が USB 接続されていることを確認:

```bash
adb devices
```

期待: 端末 serial が 1 行。

```bash
scripts/ros2/build_helper.sh
dotnet run --project tools/rosettadds-perf-runner -- --build-target Android --scenario unity-to-ros2-reliable-32 --artifacts artifacts/perf-android-smoke
```

期待: `artifacts/perf-android-smoke/<run-id>/unity-to-ros2-reliable-32/manifest.json` に `playerExitCode=0, helperExitCode=0` が記録され、`metrics.ndjson` に `start` / `ready` / `matched` / `measure_start` / `measure_done` イベントが現れる。

失敗時:
- 端末が adb に見えない: 端末側で USB デバッグを ON にし、「この PC を信頼」を承認。
- `install` 失敗: 既存インストールとの signature 衝突。`adb uninstall com.ojii3.rosettadds.perf` してから再実行。
- `matched` タイムアウト: 端末と host が同じ AP にいるか、`multicast` が AP で許可されているかを確認。`adb shell` から `ping <host ip>` で疎通確認。
- 起動しない: `adb logcat -d -s Unity:*` でクラッシュ原因を調査。

- [ ] **Step 15.6: 既存 desktop 経路の regression smoke**

```bash
dotnet run --project tools/rosettadds-perf-runner -- --build-target StandaloneLinux64 --scenario unity-to-ros2-reliable-32 --artifacts artifacts/perf-desktop-regression
```

期待: 既存の desktop scenario が無改修で全 scenario 完走すること。

- [ ] **Step 15.7: コミットログ確認と PR 準備**

```bash
git log --oneline 791bfc1..HEAD
```

期待: Task 1-14 までのコミットが積まれている。`git status` clean。

- [ ] **Step 15.8: 動作確認 OK なら push + PR 作成**

`AGENTS.md` 通り、`main` ではなく `feat/android-il2cpp-perf-runner` から PR を作成する。レビュー観点は:

- IProcessDriver / AdbClient / AndroidAdbDriver の責務分離
- helper 起動時の `ROS_LOCALHOST_ONLY` 切替が desktop 経路に副作用を及ぼしていないこと
- EditMode テスト (3 件) と .NET テスト (15+ 件) の網羅
- spec との整合 (Out of scope に書いた項目を実装に含めていないか)

---

## 完了条件 (Definition of Done)

- [ ] Task 1-14 の TDD サイクルがすべて green
- [ ] `dotnet build rosettadds.sln` 0 warning、0 error
- [ ] `dotnet test rosettadds.sln` すべて PASS
- [ ] `scripts/unity/run_editmode.sh` すべて PASS
- [ ] Android 経路で 1 scenario が end-to-end 完走 (`metrics.ndjson` に `start` 〜 `measure_done` が揃う)
- [ ] 既存 desktop 経路が無改修で完走
- [ ] `applicationIdentifier.Android` が `com.ojii3.rosettadds.perf` になっている
- [ ] helper 起動時の `ROS_LOCALHOST_ONLY` が build target 連動で 0/1 切替する
- [ ] `git status` clean
- [ ] PR が `feat/android-il2cpp-perf-runner` から `main` 宛で出ている

## スコープ外 (本 plan では実施しない)

- 複数 Android デバイスのパラレル / マルチ fanout 計測。
- adb over WiFi (USB 接続前提)。
- スクショ / 動画 / Android Studio Profiler live attach。
- Android Emulator 計測。
- Android 上のメモリリーク guard (IL2CPP の GC 挙動は Mono と違うので別途検討)。
- ユニキャスト discovery 経路 (`ROS_STATIC_PEERS` 等)。マルチキャストが
  通らないネットワーク形態は別 PR。
- Gradle 経由の player ビルド (Unity 6 既定の internal build で進める)。
- Player 側の `DesktopProcessDriver` 完全 refactor (Task 13 では helper 側のみ
  driver 経由に切替え、Player 側は `ProcessCapture` 直叩きのまま残す。完全
  refactor は別 PR)。
