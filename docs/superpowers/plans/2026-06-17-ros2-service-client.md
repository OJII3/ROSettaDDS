# ROS 2 Service Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ROSettaDDS から ROS 2 (Fast DDS) のサービスサーバを呼び出せるようにする（`.srv` 自動生成 + Fast DDS の related_sample_identity 相関 + 型付きクライアント API）。

**Architecture:** 既存の pub/sub 基盤（`DomainParticipant` / `StatefulWriter` / `StatefulReader` / SEDP）をそのまま再利用する。サービスは request 用トピック (`rq/<svc>Request`) への Publisher と reply 用トピック (`rr/<svc>Reply`) の Reader の組として実装する。リクエストとレスポンスの相関は、reply DATA の inline QoS に載る `related_sample_identity`（PID 0x800f / 0x0083）= `SampleIdentity`(request writer GUID + writerSN) で行う。`.srv` は既存 MsgGen を拡張して 2 つの message + サービス記述子に変換する。

**Tech Stack:** C# / .NET (netstandard2.1), xUnit + FluentAssertions, Roslyn Incremental Source Generator。ビルド `dotnet build rosettadds.sln`、テスト `dotnet test`。

**仕様書:** `docs/superpowers/specs/2026-06-17-ros2-service-client-design.md`

**ブランチ:** `feat/service-client`（作成済み）

---

## ファイル構成

新規作成:
- `src/rosettadds/Common/SampleIdentity.cs` — GUID + SequenceNumber の値型（24B wire）
- `src/rosettadds/Cdr/ParameterList/RelatedSampleIdentityInlineQos.cs` — related_sample_identity の inline QoS read/write
- `src/rosettadds/Dds/ServiceDescriptor.cs` — `ServiceDescriptor<TReq,TResp>`（型名 + シリアライザの束）
- `src/rosettadds/Dds/ServiceClient.cs` — `ServiceClient<TReq,TResp>`
- `src/ROSettaDDS.MsgGen/Parsing/SrvParser.cs` — `.srv` パーサ
- `src/ROSettaDDS.MsgGen/Emitting/ServiceDescriptorEmitter.cs` — 記述子 C# 生成
- `tests/rosettadds.Tests/Rcl/ServiceNamingTests.cs`
- `tests/rosettadds.Tests/Common/SampleIdentityTests.cs`
- `tests/rosettadds.Tests/Cdr/RelatedSampleIdentityInlineQosTests.cs`
- `tests/rosettadds.Tests/MsgGen/SrvParserTests.cs`
- `tests/rosettadds.Tests/Integration/ServiceClientLoopbackTests.cs`

変更:
- `src/ROSettaDDS.MsgGen/Model/MessageDefinition.cs` — `SubNamespace` 追加
- `src/ROSettaDDS.MsgGen/TypeMapping/TypeNameResolver.cs` — subNs 対応の型名生成
- `src/ROSettaDDS.MsgGen/Emitting/CSharpEmitter.cs` — `def.SubNamespace` を使う
- `tools/rosettadds-genmsg/Program.cs` — `.srv` 走査
- `src/ROSettaDDS.SourceGenerator/MsgSourceGenerator.cs` — `.srv` 走査
- `src/rosettadds/Cdr/ParameterList/ParameterId.cs` — PID 定数追加
- `src/rosettadds/Rtps/HistoryCache/CacheChange.cs` — InlineQos 追加
- `src/rosettadds/Rtps/Reader/StatefulReader.cs` — CacheChange へ inline QoS 伝播
- `src/rosettadds/Rtps/Writer/StatefulWriter.cs` — SN を返す Write
- `src/rosettadds/Dds/Publisher.cs` — SN を返す Publish
- `src/rosettadds/Dds/ReliableUserReader.cs` — `SampleReceived`(CacheChange) イベント
- `src/rosettadds/Rcl/Naming/TopicNameMangler.cs` — service mangle
- `src/rosettadds/Dds/DomainParticipant.cs` — `CreateServiceClient` + reply reader 内部ヘルパ
- `docs/interop.md` — service 検証手順

---

## Phase A: `.srv` コード生成

### Task A1: MessageDefinition にサブ名前空間を追加

**Files:**
- Modify: `src/ROSettaDDS.MsgGen/Model/MessageDefinition.cs`
- Test: `tests/rosettadds.Tests/MsgGen/SrvParserTests.cs`（後続タスクで使用。本タスクではビルドのみ）

- [ ] **Step 1: 実装を変更**

`MessageDefinition` に `SubNamespace`（既定 `"msg"`）を追加し、`RosTypeName` がそれを使うようにする。コンストラクタにオプション引数を足す（既存呼び出しは `"msg"` 既定で不変）。

```csharp
    public MessageDefinition(
        string package,
        string name,
        IReadOnlyList<MessageConstant> constants,
        IReadOnlyList<MessageField> fields,
        string subNamespace = "msg")
    {
        Package = package;
        Name = name;
        Constants = constants;
        Fields = fields;
        SubNamespace = subNamespace;
    }

    /// <summary>サブ名前空間 ("msg" または "srv")。</summary>
    public string SubNamespace { get; }

    /// <summary>ROS 2 型名 (例 "std_msgs/msg/Header", "example_interfaces/srv/AddTwoInts_Request")。</summary>
    public string RosTypeName => $"{Package}/{SubNamespace}/{Name}";
```

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/ROSettaDDS.MsgGen/ROSettaDDS.MsgGen.csproj`
Expected: 成功（既存 `.msg` 経路は subNamespace="msg" 既定で挙動不変）。

- [ ] **Step 3: コミット**

```bash
git add src/ROSettaDDS.MsgGen/Model/MessageDefinition.cs
git commit -m "feat(msggen): MessageDefinition にサブ名前空間 (msg/srv) を追加"
```

---

### Task A2: TypeNameResolver を subNamespace 対応に

**Files:**
- Modify: `src/ROSettaDDS.MsgGen/TypeMapping/TypeNameResolver.cs`
- Modify: `src/ROSettaDDS.MsgGen/Emitting/CSharpEmitter.cs`

- [ ] **Step 1: TypeNameResolver にオーバーロード追加**

既存 `RosTypeName(package, name)` / `DdsTypeName(package, name)` は `"msg"` 固定。subNs 引数版を追加し、既存メソッドはそれへ委譲する。DDS 型名はメッセージ名（`AddTwoInts_Request` のようにアンダースコアを含む生の ROS 名）をそのまま使うのが要点。

```csharp
    /// <summary>ROS 2 型名 (例 "std_msgs/msg/Header")。</summary>
    public string RosTypeName(string package, string rosName) => RosTypeName(package, "msg", rosName);

    public string RosTypeName(string package, string subNamespace, string rosName)
        => $"{package}/{subNamespace}/{rosName}";

    /// <summary>DDS 型名 (例 "std_msgs::msg::dds_::Header_")。</summary>
    public string DdsTypeName(string package, string rosName) => DdsTypeName(package, "msg", rosName);

    public string DdsTypeName(string package, string subNamespace, string rosName)
        => $"{package}::{subNamespace}::dds_::{rosName}_";
```

- [ ] **Step 2: CSharpEmitter が def.SubNamespace を使うよう変更**

`EmitStruct` 内の 2 行を subNs 対応版へ差し替える。

変更前:
```csharp
        sb.Append($"    public const string RosTypeName = \"{_resolver.RosTypeName(def.Package, def.Name)}\";\n");
        sb.Append($"    public const string DdsTypeName = \"{_resolver.DdsTypeName(def.Package, def.Name)}\";\n");
```
変更後:
```csharp
        sb.Append($"    public const string RosTypeName = \"{_resolver.RosTypeName(def.Package, def.SubNamespace, def.Name)}\";\n");
        sb.Append($"    public const string DdsTypeName = \"{_resolver.DdsTypeName(def.Package, def.SubNamespace, def.Name)}\";\n");
```

- [ ] **Step 3: 既存 msg 生成にドリフトが無いことを確認**

Run: `dotnet run --project tools/rosettadds-genmsg -- --input msgs --output src/rosettadds/Msgs --check`
Expected: `0 drifted`（既存 std_msgs/geometry_msgs などの生成結果が変わらない）。

> 注: `msgs` ディレクトリが無い / レイアウトが違う場合は、リポジトリの genmsg 実行方法（README「Generating custom messages」）に従い入力パスを合わせる。`--check` が `0 drifted` を返せば回帰なし。

- [ ] **Step 4: コミット**

```bash
git add src/ROSettaDDS.MsgGen/TypeMapping/TypeNameResolver.cs src/ROSettaDDS.MsgGen/Emitting/CSharpEmitter.cs
git commit -m "feat(msggen): 型名生成を subNamespace 対応にする"
```

---

### Task A3: SrvParser を実装

**Files:**
- Create: `src/ROSettaDDS.MsgGen/Parsing/SrvParser.cs`
- Test: `tests/rosettadds.Tests/MsgGen/SrvParserTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

```csharp
using ROSettaDDS.MsgGen.Parsing;

namespace ROSettaDDS.Tests.MsgGen;

public class SrvParserTests
{
    private const string AddTwoInts = "int64 a\nint64 b\n---\nint64 sum\n";

    [Fact]
    public void Parse_は_request_と_response_の_2定義を返す()
    {
        var (request, response) = SrvParser.Parse("example_interfaces", "AddTwoInts", AddTwoInts);

        request.Name.Should().Be("AddTwoInts_Request");
        request.SubNamespace.Should().Be("srv");
        request.RosTypeName.Should().Be("example_interfaces/srv/AddTwoInts_Request");
        request.Fields.Select(f => f.Name).Should().Equal("a", "b");

        response.Name.Should().Be("AddTwoInts_Response");
        response.SubNamespace.Should().Be("srv");
        response.Fields.Select(f => f.Name).Should().Equal("sum");
    }

    [Fact]
    public void Parse_は_空の_request_response_を許容する()
    {
        var (request, response) = SrvParser.Parse("std_srvs", "Empty", "---\n");

        request.Fields.Should().BeEmpty();
        response.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Parse_は_区切りが無いと例外()
    {
        Action act = () => SrvParser.Parse("p", "X", "int64 a\nint64 b\n");

        act.Should().Throw<MsgParseException>();
    }
}
```

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter SrvParserTests`
Expected: FAIL（`SrvParser` 未定義でコンパイルエラー）。

- [ ] **Step 3: 最小実装**

`---` 単独行（前後空白許容）で 2 分割し、各半分を `MsgParser.Parse` に渡す。message 名は `<Name>_Request` / `<Name>_Response`、subNamespace は `"srv"`。

```csharp
using System;
using ROSettaDDS.MsgGen.Model;

namespace ROSettaDDS.MsgGen.Parsing;

/// <summary>
/// ROS 2 の <c>.srv</c> を request / response の 2 つの <see cref="MessageDefinition"/> に変換する。
/// <c>---</c> 単独行で request 部と response 部を分割し、各部を <see cref="MsgParser"/> で解析する。
/// </summary>
public static class SrvParser
{
    public static (MessageDefinition Request, MessageDefinition Response) Parse(
        string package, string serviceName, string text)
    {
        if (string.IsNullOrEmpty(package)) throw new ArgumentException("package is required", nameof(package));
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("serviceName is required", nameof(serviceName));

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');

        int separator = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                separator = i;
                break;
            }
        }
        if (separator < 0)
        {
            throw new MsgParseException($"{serviceName}.srv: request/response 区切り '---' が見つかりません");
        }

        string requestText = string.Join("\n", lines[..separator]);
        string responseText = string.Join("\n", lines[(separator + 1)..]);

        var request = MsgParser.Parse(package, serviceName + "_Request", requestText);
        var response = MsgParser.Parse(package, serviceName + "_Response", responseText);

        return (
            WithSrvNamespace(request),
            WithSrvNamespace(response));
    }

    private static MessageDefinition WithSrvNamespace(MessageDefinition def)
        => new(def.Package, def.Name, def.Constants, def.Fields, subNamespace: "srv");
}
```

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test --filter SrvParserTests`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/ROSettaDDS.MsgGen/Parsing/SrvParser.cs tests/rosettadds.Tests/MsgGen/SrvParserTests.cs
git commit -m "feat(msggen): .srv パーサを追加"
```

---

### Task A4: サービス記述子の C# 生成

**Files:**
- Create: `src/ROSettaDDS.MsgGen/Emitting/ServiceDescriptorEmitter.cs`
- Test: `tests/rosettadds.Tests/MsgGen/SrvParserTests.cs`（記述子生成の文字列検証を追記）

> 前提: 実行時型 `ROSettaDDS.Dds.ServiceDescriptor<TReq,TResp>` は Task C2 で作る。本タスクは生成器が正しい C# テキストを出力することのみ検証する（コンパイルは Phase C 完了後）。

- [ ] **Step 1: 失敗するテストを追記**

`SrvParserTests` に以下を追加する。

```csharp
    [Fact]
    public void ServiceDescriptorEmitter_は_記述子クラスを生成する()
    {
        var (request, response) = SrvParser.Parse("example_interfaces", "AddTwoInts", AddTwoInts);
        var resolver = new ROSettaDDS.MsgGen.TypeMapping.TypeNameResolver();

        string code = new ROSettaDDS.MsgGen.Emitting.ServiceDescriptorEmitter(resolver)
            .Emit("example_interfaces", "AddTwoInts", request, response);

        code.Should().Contain("namespace ROSettaDDS.Msgs.ExampleInterfaces");
        code.Should().Contain("public static class AddTwoIntsService");
        code.Should().Contain("ServiceDescriptor<AddTwoInts_Request, AddTwoInts_Response>");
        code.Should().Contain("AddTwoInts_RequestSerializer.Instance");
        code.Should().Contain("AddTwoInts_ResponseSerializer.Instance");
        code.Should().Contain("example_interfaces::srv::dds_::AddTwoInts_Request_");
        code.Should().Contain("example_interfaces::srv::dds_::AddTwoInts_Response_");
    }
```

（`using ROSettaDDS.MsgGen.Parsing;` は既にファイル先頭にある。）

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter ServiceDescriptorEmitter`
Expected: FAIL（`ServiceDescriptorEmitter` 未定義）。

- [ ] **Step 3: 最小実装**

`<Service>Service` 静的クラスに `Descriptor` を 1 つ持たせる。Request/Response の C# 型名・シリアライザ名・DDS 型名は `TypeNameResolver` から得る。

```csharp
using System.Text;
using ROSettaDDS.MsgGen.Model;
using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.MsgGen.Emitting;

/// <summary>
/// .srv 1 件につき <c>&lt;Service&gt;Service</c> 静的クラスを生成する。
/// このクラスは <c>ServiceDescriptor&lt;TRequest, TResponse&gt;</c> 型の <c>Descriptor</c> を公開し、
/// クライアント生成 API へ型名・シリアライザを一括で渡せるようにする。
/// </summary>
public sealed class ServiceDescriptorEmitter
{
    private readonly TypeNameResolver _resolver;

    public ServiceDescriptorEmitter(TypeNameResolver? resolver = null)
    {
        _resolver = resolver ?? new TypeNameResolver();
    }

    public string Emit(string package, string serviceName, MessageDefinition request, MessageDefinition response)
    {
        string ns = _resolver.Namespace(package);
        string reqType = _resolver.CSharpTypeName(package, request.Name);
        string respType = _resolver.CSharpTypeName(package, response.Name);
        string reqSer = _resolver.SerializerName(package, request.Name);
        string respSer = _resolver.SerializerName(package, response.Name);
        string reqDds = _resolver.DdsTypeName(package, request.SubNamespace, request.Name);
        string respDds = _resolver.DdsTypeName(package, response.SubNamespace, response.Name);
        string className = NamingConventions.ToPascalCase(serviceName) + "Service";

        var sb = new StringBuilder();
        sb.Append("// <auto-generated>\n");
        sb.Append("//     このファイルは rosettadds-genmsg により生成されました。手動で編集しないでください。\n");
        sb.Append($"//     source: {package}/srv/{serviceName}\n");
        sb.Append("// </auto-generated>\n");
        sb.Append("using ROSettaDDS.Dds;\n\n");
        sb.Append($"namespace {ns}\n{{\n");
        sb.Append($"    public static class {className}\n    {{\n");
        sb.Append($"        public static readonly ServiceDescriptor<{reqType}, {respType}> Descriptor =\n");
        sb.Append($"            new ServiceDescriptor<{reqType}, {respType}>(\n");
        sb.Append($"                requestDdsTypeName: \"{reqDds}\",\n");
        sb.Append($"                responseDdsTypeName: \"{respDds}\",\n");
        sb.Append($"                requestSerializer: {reqSer}.Instance,\n");
        sb.Append($"                responseSerializer: {respSer}.Instance);\n");
        sb.Append("    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: テスト通過を確認（文字列検証のみ）**

Run: `dotnet test --filter ServiceDescriptorEmitter`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/ROSettaDDS.MsgGen/Emitting/ServiceDescriptorEmitter.cs tests/rosettadds.Tests/MsgGen/SrvParserTests.cs
git commit -m "feat(msggen): サービス記述子の C# 生成を追加"
```

---

### Task A5: genmsg CLI で .srv を走査

**Files:**
- Modify: `tools/rosettadds-genmsg/Program.cs`

> 依存: 実行時型 `ServiceDescriptor<,>`（Task C2）が無いと、生成した記述子 .cs を含むプロジェクトはビルドできない。本タスクは「genmsg が .srv から 3 ファイル（Request/Response/Service）を出力する」ことだけを手動確認する（生成器自体のビルドは型に依存しない）。

- [ ] **Step 1: .srv 走査を追加**

`Program.cs` の 1st pass（`.msg` 解析）の直後に、`.srv` を解析して request/response を `parsed` に加え、サービス名→(req,resp) の対応も別リストに記録する。2nd pass の後に記述子ファイルを出力する。

`.srv` 解析ブロック（msgFiles ループの後に追加）:
```csharp
var srvFiles = Directory.GetFiles(input, "*.srv", SearchOption.AllDirectories);
Array.Sort(srvFiles, StringComparer.Ordinal);
var services = new List<(string Package, string Name, ROSettaDDS.MsgGen.Model.MessageDefinition Req, ROSettaDDS.MsgGen.Model.MessageDefinition Resp)>();
foreach (var file in srvFiles)
{
    string? srvDir = Path.GetDirectoryName(file);
    string? pkgDir = srvDir is null ? null : Path.GetDirectoryName(srvDir);
    if (srvDir is null || pkgDir is null || !Path.GetFileName(srvDir).Equals("srv", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Skip (expected <package>/srv/<Name>.srv layout): {file}");
        continue;
    }
    string package = Path.GetFileName(pkgDir);
    string name = Path.GetFileNameWithoutExtension(file);
    var (req, resp) = ROSettaDDS.MsgGen.Parsing.SrvParser.Parse(package, name, File.ReadAllText(file));
    parsed.Add(req);
    parsed.Add(resp);
    services.Add((package, name, req, resp));
}
```

記述子出力（2nd pass の `foreach (var def in parsed)` ループの後に追加）:
```csharp
var descriptorEmitter = new ROSettaDDS.MsgGen.Emitting.ServiceDescriptorEmitter(resolver);
foreach (var svc in services)
{
    string code = descriptorEmitter.Emit(svc.Package, svc.Name, svc.Req, svc.Resp);
    string subNs = resolver.SubNamespace(svc.Package);
    string outDir = Path.Combine(output, subNs);
    string outPath = Path.Combine(outDir, svc.Name + "Service.cs");
    total++;
    string? existing = File.Exists(outPath) ? File.ReadAllText(outPath) : null;
    bool differs = !string.Equals(existing, code, StringComparison.Ordinal);
    if (check) { if (differs) { changed++; Console.Error.WriteLine($"DRIFT: {outPath}"); } continue; }
    if (differs) { Directory.CreateDirectory(outDir); File.WriteAllText(outPath, code); changed++; Console.WriteLine($"generated: {outPath}"); }
}
```

- [ ] **Step 2: genmsg のビルド確認**

Run: `dotnet build tools/rosettadds-genmsg/rosettadds-genmsg.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add tools/rosettadds-genmsg/Program.cs
git commit -m "feat(genmsg): .srv を走査して Request/Response/記述子を生成する"
```

---

### Task A6: SourceGenerator で .srv を走査

**Files:**
- Modify: `src/ROSettaDDS.SourceGenerator/MsgSourceGenerator.cs`

- [ ] **Step 1: .srv も入力に含める**

`AdditionalTextsProvider` のフィルタを `.msg` / `.srv` 両対応にし、`MsgInput` に「これは srv か」を持たせて `GenerateAll` で分岐する。

フィルタ変更:
```csharp
        var inputs = context.AdditionalTextsProvider
            .Where(static a => a.Path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)
                            || a.Path.EndsWith(".srv", StringComparison.OrdinalIgnoreCase))
```

`MsgInput` に `IsSrv` を追加（`Path` 末尾で判定するヘルパでも可）。`GenerateAll` の解析ループを分岐:
```csharp
        var services = new List<(string Package, string Name, MessageDefinition Req, MessageDefinition Resp)>();
        foreach (var input in inputs)
        {
            string name = Path.GetFileNameWithoutExtension(input.Path);
            string package = !string.IsNullOrEmpty(input.Package) ? input.Package! : InferPackage(input.Path);
            try
            {
                if (input.Path.EndsWith(".srv", StringComparison.OrdinalIgnoreCase))
                {
                    var (req, resp) = SrvParser.Parse(package, name, input.Text);
                    parsed.Add((input, package, req.Name, req));
                    parsed.Add((input, package, resp.Name, resp));
                    services.Add((package, name, req, resp));
                }
                else
                {
                    var def = MsgParser.Parse(package, name, input.Text);
                    parsed.Add((input, package, name, def));
                }
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, ex.Message));
            }
        }
```

emit ループの後に記述子出力:
```csharp
        var descriptorEmitter = new ServiceDescriptorEmitter(resolver);
        foreach (var svc in services)
        {
            try
            {
                string code = descriptorEmitter.Emit(svc.Package, svc.Name, svc.Req, svc.Resp);
                string hint = $"{svc.Package}_{NamingConventions.ToPascalCase(svc.Name)}Service.g.cs";
                context.AddSource(hint, SourceText.From(code, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, ex.Message));
            }
        }
```

`InferPackage` は `srv` ディレクトリ名も許容するよう、`"msg"` 判定を `"msg" or "srv"` に緩める:
```csharp
        if (msgDir is not null && parent is not null &&
            (string.Equals(Path.GetFileName(msgDir), "msg", StringComparison.Ordinal)
             || string.Equals(Path.GetFileName(msgDir), "srv", StringComparison.Ordinal)))
```

先頭に `using ROSettaDDS.MsgGen.TypeMapping;`（`NamingConventions`）が無ければ追加する。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/ROSettaDDS.SourceGenerator/ROSettaDDS.SourceGenerator.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/ROSettaDDS.SourceGenerator/MsgSourceGenerator.cs
git commit -m "feat(sourcegen): .srv を走査して Request/Response/記述子を生成する"
```

---

## Phase B: wire プリミティブ

### Task B1: SampleIdentity 値型

**Files:**
- Create: `src/rosettadds/Common/SampleIdentity.cs`
- Test: `tests/rosettadds.Tests/Common/SampleIdentityTests.cs`

`Common/Guid.cs` のシグネチャ確認: `Guid(GuidPrefix, EntityId)`、`Guid.WriteTo(Span<byte>)`（16B, big-endian 固定）/ `Guid.Read(...)`。実装前に `src/rosettadds/Common/Guid.cs` を開いて `WriteTo` / `Read` の正確なシグネチャ（エンディアン引数の有無）を確認すること。下記は GuidPrefix(12B) + EntityId(4B) が固定バイト列であることを前提にしている。

- [ ] **Step 1: 失敗するテストを書く**

```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Common;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Common;

public class SampleIdentityTests
{
    [Fact]
    public void WriteTo_と_Read_は往復する_LE()
    {
        var guid = new Guid(GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS), new EntityId(0x05u, EntityKind.UserDefinedWriterNoKey));
        var id = new SampleIdentity(guid, new SequenceNumber(42));

        Span<byte> buf = stackalloc byte[SampleIdentity.Size];
        id.WriteTo(buf, littleEndian: true);
        var read = SampleIdentity.Read(buf, littleEndian: true);

        read.Should().Be(id);
    }

    [Fact]
    public void Size_は_24バイト()
    {
        SampleIdentity.Size.Should().Be(24);
    }
}
```

> 注: `VendorId.ROSettaDDS` の正確な定数名は `src/rosettadds/Common/VendorId.cs` を確認して合わせる。

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter SampleIdentityTests`
Expected: FAIL（`SampleIdentity` 未定義）。

- [ ] **Step 3: 実装**

GUID は RTPS 上 16B 固定バイト列（エンディアン非依存）、SequenceNumber は 8B (high int32, low uint32) でエンディアン依存。

```csharp
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Common;

/// <summary>
/// Fast DDS RPC の SampleIdentity (writer GUID + sequence number)。wire 上 24 バイト。
/// サービスの request/reply 相関 (related_sample_identity) に使う。
/// </summary>
public readonly struct SampleIdentity : IEquatable<SampleIdentity>
{
    public const int Size = 24; // Guid 16 + SequenceNumber 8

    public Guid Writer { get; }
    public SequenceNumber SequenceNumber { get; }

    public SampleIdentity(Guid writer, SequenceNumber sequenceNumber)
    {
        Writer = writer;
        SequenceNumber = sequenceNumber;
    }

    public void WriteTo(Span<byte> destination, bool littleEndian)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination requires at least {Size} bytes.", nameof(destination));
        Writer.WriteTo(destination[..16]);
        SequenceNumber.WriteTo(destination.Slice(16, 8), littleEndian);
    }

    public static SampleIdentity Read(ReadOnlySpan<byte> source, bool littleEndian)
    {
        if (source.Length < Size)
            throw new ArgumentException($"Source requires at least {Size} bytes.", nameof(source));
        var writer = Guid.Read(source[..16]);
        var sn = SequenceNumber.Read(source.Slice(16, 8), littleEndian);
        return new SampleIdentity(writer, sn);
    }

    public bool Equals(SampleIdentity other) => Writer.Equals(other.Writer) && SequenceNumber.Equals(other.SequenceNumber);
    public override bool Equals(object? obj) => obj is SampleIdentity s && Equals(s);
    public override int GetHashCode() => System.HashCode.Combine(Writer, SequenceNumber);
    public override string ToString() => $"SampleIdentity({Writer}, {SequenceNumber})";

    public static bool operator ==(SampleIdentity left, SampleIdentity right) => left.Equals(right);
    public static bool operator !=(SampleIdentity left, SampleIdentity right) => !left.Equals(right);
}
```

> `Guid.WriteTo(Span<byte>)` / `Guid.Read(ReadOnlySpan<byte>)` の実シグネチャに合わせて呼び出しを調整する。エンディアン引数を取る場合は GUID は big-endian 固定で渡す。`System.HashCode` が netstandard2.1 で使えない場合は `Writer.GetHashCode() ^ SequenceNumber.GetHashCode()` に置換。

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test --filter SampleIdentityTests`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Common/SampleIdentity.cs tests/rosettadds.Tests/Common/SampleIdentityTests.cs
git commit -m "feat(common): SampleIdentity 値型を追加"
```

---

### Task B2: ParameterId に related_sample_identity を追加

**Files:**
- Modify: `src/rosettadds/Cdr/ParameterList/ParameterId.cs`

- [ ] **Step 1: PID 定数を追加**

`// Inline QoS / Endpoint` セクションに追記する。

```csharp
    /// <summary>Fast DDS related_sample_identity (新)。サービス reply の inline QoS で使用。</summary>
    public const ushort RelatedSampleIdentity = 0x800F;

    /// <summary>Fast DDS related_sample_identity (レガシ。読み取りフォールバック用)。</summary>
    public const ushort RelatedSampleIdentityLegacy = 0x0083;
```

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Cdr/ParameterList/ParameterId.cs
git commit -m "feat(cdr): related_sample_identity の PID 定数を追加"
```

---

### Task B3: related_sample_identity の inline QoS read/write

**Files:**
- Create: `src/rosettadds/Cdr/ParameterList/RelatedSampleIdentityInlineQos.cs`
- Test: `tests/rosettadds.Tests/Cdr/RelatedSampleIdentityInlineQosTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Cdr;

public class RelatedSampleIdentityInlineQosTests
{
    private static SampleIdentity Sample()
    {
        var guid = new Guid(GuidPrefix.CreateForCurrentProcess(VendorId.ROSettaDDS), new EntityId(0x05u, EntityKind.UserDefinedWriterNoKey));
        return new SampleIdentity(guid, new SequenceNumber(7));
    }

    [Fact]
    public void Build_した_inlineQos_は_TryRead_で復元できる()
    {
        var id = Sample();
        var inlineQos = RelatedSampleIdentityInlineQos.Build(id, CdrEndianness.LittleEndian);

        RelatedSampleIdentityInlineQos.TryRead(inlineQos, CdrEndianness.LittleEndian, out var read)
            .Should().BeTrue();
        read.Should().Be(id);
    }

    [Fact]
    public void TryRead_は_related_identity_が無ければ_false()
    {
        var empty = DataSubmessage.BuildStatusInfoInlineQos(0u, CdrEndianness.LittleEndian);

        RelatedSampleIdentityInlineQos.TryRead(empty, CdrEndianness.LittleEndian, out _)
            .Should().BeFalse();
    }
}
```

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter RelatedSampleIdentityInlineQos`
Expected: FAIL（型未定義）。

- [ ] **Step 3: 実装**

`DataSubmessage.BuildStatusInfoInlineQos` と同じ書式で PID 0x800F のパラメータを書く。読み取りは 0x800F / 0x0083 両方を受理する（`ParameterId.StripFlags` で must-understand ビットを除去して比較）。

```csharp
using ROSettaDDS.Common;

namespace ROSettaDDS.Cdr.ParameterList;

/// <summary>
/// サービス reply の inline QoS に載る related_sample_identity (Fast DDS RPC) の組み立て・解析。
/// 書き込みは PID 0x800F、読み取りは 0x800F / 0x0083 (レガシ) の双方を受理する。
/// </summary>
public static class RelatedSampleIdentityInlineQos
{
    /// <summary>related_sample_identity を含む PL_CDR inline QoS (SENTINEL 込み) を生成する。</summary>
    public static byte[] Build(SampleIdentity identity, CdrEndianness endianness)
    {
        Span<byte> buffer = stackalloc byte[32]; // PID(2)+len(2)+24 + sentinel(4) = 32
        var writer = new CdrWriter(buffer, endianness);
        var pl = new ParameterListWriter(writer);
        pl.BeginParameter(ParameterId.RelatedSampleIdentity);
        Span<byte> idBytes = stackalloc byte[SampleIdentity.Size];
        identity.WriteTo(idBytes, endianness == CdrEndianness.LittleEndian);
        pl.WriteRawBytes(idBytes);
        pl.EndParameter();
        pl.WriteSentinel();

        var current = pl.CurrentWriter;
        var copy = new byte[current.Position];
        current.WrittenSpan.CopyTo(copy);
        return copy;
    }

    /// <summary>inline QoS から related_sample_identity を読み出す。</summary>
    public static bool TryRead(ReadOnlySpan<byte> inlineQos, CdrEndianness endianness, out SampleIdentity identity)
    {
        identity = default;
        if (inlineQos.IsEmpty)
        {
            return false;
        }
        var reader = new CdrReader(inlineQos, endianness);
        var pl = new ParameterListReader(reader);
        while (pl.MoveNext(out var pid, out _))
        {
            ushort stripped = ParameterId.StripFlags(pid);
            if (stripped != ParameterId.RelatedSampleIdentity && stripped != ParameterId.RelatedSampleIdentityLegacy)
            {
                continue;
            }
            var raw = pl.CurrentValueRaw();
            if (raw.Length < SampleIdentity.Size)
            {
                return false;
            }
            identity = SampleIdentity.Read(raw, endianness == CdrEndianness.LittleEndian);
            return true;
        }
        return false;
    }
}
```

> `ParameterListWriter` が `WriteRawBytes` を持つことは確認済み。`stackalloc byte[32]` で足りない場合（実測で SampleIdentity 値は 24B、PID/len/sentinel 込みで 32B 丁度）は配列確保に切り替える。

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test --filter RelatedSampleIdentityInlineQos`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Cdr/ParameterList/RelatedSampleIdentityInlineQos.cs tests/rosettadds.Tests/Cdr/RelatedSampleIdentityInlineQosTests.cs
git commit -m "feat(cdr): related_sample_identity の inline QoS read/write を追加"
```

---

## Phase C: ランタイム配線

### Task C1: CacheChange に inline QoS を持たせ、StatefulReader で伝播

**Files:**
- Modify: `src/rosettadds/Rtps/HistoryCache/CacheChange.cs`
- Modify: `src/rosettadds/Rtps/Reader/StatefulReader.cs`

- [ ] **Step 1: CacheChange に InlineQos を追加**

既存コンストラクタはそのまま残し（inline QoS 無し既定）、フィールドを追加する。

```csharp
    public ReadOnlyMemory<byte> InlineQos { get; }
    public Cdr.CdrEndianness InlineQosEndianness { get; }

    public CacheChange(
        ChangeKind kind,
        Guid writerGuid,
        SequenceNumber sequenceNumber,
        Time sourceTimestamp,
        ReadOnlyMemory<byte> serializedPayload,
        ReadOnlyMemory<byte> inlineQos = default,
        Cdr.CdrEndianness inlineQosEndianness = Cdr.CdrEndianness.LittleEndian)
    {
        Kind = kind;
        WriterGuid = writerGuid;
        SequenceNumber = sequenceNumber;
        SourceTimestamp = sourceTimestamp;
        SerializedPayload = serializedPayload;
        InlineQos = inlineQos;
        InlineQosEndianness = inlineQosEndianness;
    }
```

ファイル先頭に `using ROSettaDDS.Cdr;` が無ければ追加し、上記の `Cdr.CdrEndianness` を `CdrEndianness` に簡略化してもよい。

- [ ] **Step 2: StatefulReader.OnData で inline QoS を渡す**

`OnData` の `new CacheChange(...)` を以下に変更（`data.InlineQos` と `endianness` を渡す）:

```csharp
            var change = new CacheChange(
                kind,
                writerGuid,
                data.WriterSequenceNumber,
                ctx.Timestamp ?? Time.Zero,
                data.SerializedPayload,
                data.InlineQos,
                endianness);
```

`OnDataFrag` も同様に `completed.Value.InlineQos` と `completed.Value.InlineQosEndianness` を渡す:

```csharp
            var change = new CacheChange(
                kind,
                writerGuid,
                dataFrag.WriterSequenceNumber,
                ctx.Timestamp ?? Time.Zero,
                completed.Value.Payload,
                completed.Value.InlineQos,
                completed.Value.InlineQosEndianness);
```

- [ ] **Step 3: 既存テストが緑のままか確認**

Run: `dotnet test --filter "Rtps|Integration"`
Expected: PASS（inline QoS 追加は後方互換。既存 pub/sub 経路に影響しない）。

- [ ] **Step 4: コミット**

```bash
git add src/rosettadds/Rtps/HistoryCache/CacheChange.cs src/rosettadds/Rtps/Reader/StatefulReader.cs
git commit -m "feat(rtps): CacheChange に inline QoS を持たせ reader で伝播する"
```

---

### Task C2: ServiceDescriptor 実行時型

**Files:**
- Create: `src/rosettadds/Dds/ServiceDescriptor.cs`

- [ ] **Step 1: 実装**

```csharp
using ROSettaDDS.Cdr;

namespace ROSettaDDS.Dds;

/// <summary>
/// 1 つの ROS 2 サービス型の DDS 型名と Request/Response シリアライザを束ねる記述子。
/// <c>.srv</c> から生成される <c>&lt;Service&gt;Service.Descriptor</c> として提供される。
/// </summary>
public sealed class ServiceDescriptor<TRequest, TResponse>
{
    public string RequestDdsTypeName { get; }
    public string ResponseDdsTypeName { get; }
    public ICdrSerializer<TRequest> RequestSerializer { get; }
    public ICdrSerializer<TResponse> ResponseSerializer { get; }

    public ServiceDescriptor(
        string requestDdsTypeName,
        string responseDdsTypeName,
        ICdrSerializer<TRequest> requestSerializer,
        ICdrSerializer<TResponse> responseSerializer)
    {
        RequestDdsTypeName = requestDdsTypeName;
        ResponseDdsTypeName = responseDdsTypeName;
        RequestSerializer = requestSerializer;
        ResponseSerializer = responseSerializer;
    }
}
```

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Dds/ServiceDescriptor.cs
git commit -m "feat(dds): ServiceDescriptor 実行時型を追加"
```

---

### Task C3: 書き込み SN を返す Write / Publish

**Files:**
- Modify: `src/rosettadds/Rtps/Writer/StatefulWriter.cs`
- Modify: `src/rosettadds/Dds/Publisher.cs`

- [ ] **Step 1: StatefulWriter に SN を返すオーバーロードを追加**

既存 `WriteAsync(payload, kind, ct)` の直後に追加する。`_history.Add` が返す `CacheChange.SequenceNumber` を返す。

```csharp
    /// <summary>
    /// <see cref="WriteAsync(ReadOnlyMemory{byte}, ChangeKind, CancellationToken)"/> と同じだが、
    /// 採番された RTPS シーケンス番号を返す。サービスの request/reply 相関に使う。
    /// </summary>
    public async ValueTask<SequenceNumber> WriteReturningSequenceNumberAsync(
        ReadOnlyMemory<byte> serializedPayload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var change = _history.Add(ChangeKind.Alive, serializedPayload, Time.Now());
        await SendDataAsync(change, cancellationToken).ConfigureAwait(false);
        return change.SequenceNumber;
    }
```

- [ ] **Step 2: Publisher に SN を返す Publish を追加**

```csharp
    /// <summary>値を送信し、採番された RTPS シーケンス番号を返す (サービス用)。</summary>
    public async ValueTask<SequenceNumber> PublishReturningSequenceNumberAsync(
        T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = SerializeWithEncapsulation(value);
        return await _writer.WriteReturningSequenceNumberAsync(payload, cancellationToken).ConfigureAwait(false);
    }
```

`Publisher.cs` 先頭に `using ROSettaDDS.Common;`（`SequenceNumber`）が無ければ追加する。

- [ ] **Step 3: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 4: コミット**

```bash
git add src/rosettadds/Rtps/Writer/StatefulWriter.cs src/rosettadds/Dds/Publisher.cs
git commit -m "feat(dds): 書き込み SN を返す Write/Publish を追加"
```

---

### Task C4: ReliableUserReader に CacheChange イベントを追加

**Files:**
- Modify: `src/rosettadds/Dds/ReliableUserReader.cs`

- [ ] **Step 1: SampleReceived イベントを追加**

既存の `PayloadReceived`（`(payload, prefix)`、Subscription 用）は維持しつつ、内側 `StatefulReader` の `CacheChange` をそのまま転送する `SampleReceived` を追加する。

`OnSampleReceived` を以下に変更:
```csharp
    /// <summary>inline QoS を含む CacheChange を必要とする利用者向け (サービス reply 等)。</summary>
    public event Action<CacheChange>? SampleReceived;

    private void OnSampleReceived(CacheChange change)
    {
        PayloadReceived?.Invoke(change.SerializedPayload, change.WriterGuid.Prefix);
        SampleReceived?.Invoke(change);
    }
```

`ReliableUserReader.cs` 先頭に `using ROSettaDDS.Rtps.HistoryCache;` が無ければ追加する。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Dds/ReliableUserReader.cs
git commit -m "feat(dds): ReliableUserReader に CacheChange イベントを追加"
```

---

### Task C5: service トピック mangle

**Files:**
- Modify: `src/rosettadds/Rcl/Naming/TopicNameMangler.cs`
- Test: `tests/rosettadds.Tests/Rcl/ServiceNamingTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

```csharp
using ROSettaDDS.Rcl.Naming;

namespace ROSettaDDS.Tests.Rcl;

public class ServiceNamingTests
{
    [Theory]
    [InlineData("add_two_ints", "rq/add_two_intsRequest")]
    [InlineData("/add_two_ints", "rq/add_two_intsRequest")]
    [InlineData("/ns/svc", "rq/ns/svcRequest")]
    public void MangleServiceRequest_は_rq_prefix_と_Request_suffix(string input, string expected)
    {
        TopicNameMangler.MangleServiceRequest(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("add_two_ints", "rr/add_two_intsReply")]
    [InlineData("/ns/svc", "rr/ns/svcReply")]
    public void MangleServiceReply_は_rr_prefix_と_Reply_suffix(string input, string expected)
    {
        TopicNameMangler.MangleServiceReply(input).Should().Be(expected);
    }
}
```

- [ ] **Step 2: 失敗を確認**

Run: `dotnet test --filter ServiceNamingTests`
Expected: FAIL（メソッド未定義）。

- [ ] **Step 3: 実装**

`TopicNameMangler` に追加する。

```csharp
    /// <summary>ROS 2 サービス名から request トピックの RTPS 名へ ("rq/&lt;name&gt;Request")。</summary>
    public static string MangleServiceRequest(string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Value cannot be null or empty.", nameof(serviceName));
        return ServiceRequestPrefix + serviceName.TrimStart('/') + "Request";
    }

    /// <summary>ROS 2 サービス名から reply トピックの RTPS 名へ ("rr/&lt;name&gt;Reply")。</summary>
    public static string MangleServiceReply(string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Value cannot be null or empty.", nameof(serviceName));
        return ServiceReplyPrefix + serviceName.TrimStart('/') + "Reply";
    }
```

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test --filter ServiceNamingTests`
Expected: PASS

- [ ] **Step 5: コミット**

```bash
git add src/rosettadds/Rcl/Naming/TopicNameMangler.cs tests/rosettadds.Tests/Rcl/ServiceNamingTests.cs
git commit -m "feat(rcl): サービス request/reply トピック mangle を追加"
```

---

### Task C6: DomainParticipant に reply reader 内部ヘルパを追加

**Files:**
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`

reply reader は Reliable 固定。`CreateSubscription` の reader 生成・SEDP 広告・`UserEndpointManager.RegisterReader` 部分を再利用したいが、戻り値が `Subscription<T>` で `ReliableUserReader` を取り出せない。そこで reader 生成部分を内部メソッドへ切り出し、サービスは具象 `ReliableUserReader` を受け取れるようにする。

- [ ] **Step 1: reader 生成を内部ヘルパへ切り出す**

`CreateSubscription` 内の「reader 生成 → endpointData 構築 → RegisterReader → SEDP 広告」を、DDS トピック名・型名・Reliability を引数に取る private メソッド `CreateReliableReplyReaderInternal` に抽出する。`CreateSubscription` はこのヘルパを呼ぶよう書き換える（既存の挙動を保つ）。

追加する private メソッド:
```csharp
    /// <summary>
    /// Reliable reader を生成し、SEDP 広告と receiver/UserEndpointManager 登録まで行う。
    /// サービス reply 用に具象 <see cref="ReliableUserReader"/> を返す。
    /// </summary>
    /// <param name="ddsTopic">既に mangle 済みの DDS トピック名 (例 "rr/add_two_intsReply")。</param>
    /// <param name="ddsTypeName">DDS 型名 (例 "example_interfaces::srv::dds_::AddTwoInts_Response_")。</param>
    private ReliableUserReader CreateReliableReplyReaderInternal(string ddsTopic, string ddsTypeName)
    {
        ThrowIfDisposed();
        var readerEntityId = _userEntityIds.AllocateReader();
        var endpointGuid = new Guid(GuidPrefix, readerEntityId);
        var reader = new ReliableUserReader(
            replyTransport: _transports.UserUnicast,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            readerEntityId: readerEntityId,
            ackNackFallbackDestination: _transports.UserMulticastDestination,
            logger: _options.Logger,
            dataFragOptions: _options.DataFragReassembly);

        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Reader,
            EndpointGuid = endpointGuid,
            ParticipantGuid = Guid,
            TopicName = ddsTopic,
            TypeName = ddsTypeName,
            Reliability = ReliabilityQos.Reliable,
            Durability = DurabilityQos.Volatile,
        };
        endpointData.UnicastLocators.AddRange(_transports.DefaultUnicastLocators);
        endpointData.MulticastLocators.Add(_transports.UserMulticastDestination);

        _userEndpoints.RegisterReader(endpointData, reader);
        _ = RunSedpOperationAsync(
            token => _sedpSubscriptionsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local service reply reader endpoint");
        return reader;
    }
```

> 必要 using: `ROSettaDDS.Dds.QoS`（`ReliabilityQos` / `DurabilityQos`）は既にファイル上部で import 済み。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 3: コミット**

```bash
git add src/rosettadds/Dds/DomainParticipant.cs
git commit -m "feat(dds): サービス reply reader 生成の内部ヘルパを追加"
```

---

### Task C7: ServiceClient 本体 + CreateServiceClient

**Files:**
- Create: `src/rosettadds/Dds/ServiceClient.cs`
- Modify: `src/rosettadds/Dds/DomainParticipant.cs`

- [ ] **Step 1: ServiceClient を実装**

request publisher は `CreatePublisher` で生成（rq トピック、Reliable/Volatile、descriptor.RequestDdsTypeName）。reply reader は `CreateReliableReplyReaderInternal` で生成し `SampleReceived` を購読。相関キーは `SampleIdentity(publisher.Guid, sentSn)`。

```csharp
using System.Collections.Concurrent;
using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.HistoryCache;

using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Dds;

/// <summary>
/// ROS 2 (Fast DDS) サービスのクライアント。request を <c>rq/&lt;svc&gt;Request</c> に publish し、
/// <c>rr/&lt;svc&gt;Reply</c> の reply を related_sample_identity で相関して返す。
/// </summary>
public sealed class ServiceClient<TRequest, TResponse> : IDisposable
{
    private readonly Publisher<TRequest> _requestPublisher;
    private readonly ReliableUserReader _replyReader;
    private readonly ServiceDescriptor<TRequest, TResponse> _descriptor;
    private readonly ILogger _logger;
    private readonly CdrReadLimits _cdrReadLimits;
    private readonly ConcurrentDictionary<SampleIdentity, TaskCompletionSource<TResponse>> _pending = new();
    private bool _disposed;

    /// <summary>request writer の RTPS GUID。相関キーの writer 部に使う。</summary>
    public Guid RequestWriterGuid => _requestPublisher.Guid;

    internal ServiceClient(
        Publisher<TRequest> requestPublisher,
        ReliableUserReader replyReader,
        ServiceDescriptor<TRequest, TResponse> descriptor,
        ILogger logger,
        CdrReadLimits cdrReadLimits)
    {
        _requestPublisher = requestPublisher;
        _replyReader = replyReader;
        _descriptor = descriptor;
        _logger = logger;
        _cdrReadLimits = cdrReadLimits;
        _replyReader.SampleReceived += OnReplyReceived;
    }

    /// <summary>マッチするサービスサーバ (rq reader と rr writer) が見つかるまで待つ。</summary>
    public async Task<bool> WaitForServiceAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsServiceReady())
            {
                return true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        return IsServiceReady();
    }

    private bool IsServiceReady()
        => _requestPublisher.Writer.MatchedReaders.Count > 0
        && _replyReader.MatchedWriterCount > 0;

    /// <summary>request を送り、相関する response を待って返す。</summary>
    public async Task<TResponse> CallAsync(
        TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var tcs = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sn = await _requestPublisher.PublishReturningSequenceNumberAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var key = new SampleIdentity(_requestPublisher.Guid, sn);
        _pending[key] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using (timeoutCts.Token.Register(static state =>
        {
            var ctx = ((ServiceClient<TRequest, TResponse> Client, SampleIdentity Key, TaskCompletionSource<TResponse> Tcs))state!;
            if (ctx.Client._pending.TryRemove(ctx.Key, out _))
            {
                ctx.Tcs.TrySetException(new TimeoutException(
                    $"Service call timed out after waiting for reply (sn={ctx.Key.SequenceNumber})."));
            }
        }, (this, key, tcs)))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private void OnReplyReceived(CacheChange change)
    {
        if (!RelatedSampleIdentityInlineQos.TryRead(change.InlineQos.Span, change.InlineQosEndianness, out var related))
        {
            _logger.Debug("ServiceClient: reply without related_sample_identity; ignored");
            return;
        }
        if (!_pending.TryRemove(related, out var tcs))
        {
            _logger.Debug($"ServiceClient: reply for unknown request {related}; ignored");
            return;
        }
        try
        {
            var response = DeserializeResponse(change.SerializedPayload.Span);
            tcs.TrySetResult(response);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private TResponse DeserializeResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CdrEncapsulation.Size)
        {
            throw new InvalidDataException(
                $"Reply payload too small for CDR encapsulation header (got {payload.Length} bytes).");
        }
        var (kind, _) = CdrEncapsulation.Read(payload[..CdrEncapsulation.Size]);
        var endian = CdrEncapsulation.GetEndianness(kind);
        var reader = new CdrReader(payload, endian, cdrOrigin: CdrEncapsulation.Size, limits: _cdrReadLimits);
        _descriptor.ResponseSerializer.Deserialize(ref reader, out var value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _replyReader.SampleReceived -= OnReplyReceived;
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(new ObjectDisposedException(nameof(ServiceClient<TRequest, TResponse>)));
        }
        _pending.Clear();
        _requestPublisher.Dispose();
        _replyReader.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
```

> `_replyReader.MatchedWriterCount` は次ステップで `ReliableUserReader` に追加する。`Publisher.Writer`（internal） / `StatefulWriter.MatchedReaders` は既存。

- [ ] **Step 2: ReliableUserReader にマッチ数を公開**

`ReliableUserReader` に追加（`_reader` は `StatefulReader`、`MatchedWriters` を持つ）:
```csharp
    public int MatchedWriterCount => _reader.MatchedWriters.Count;
```

- [ ] **Step 3: DomainParticipant.CreateServiceClient を追加**

```csharp
    /// <summary>
    /// 指定サービス名のクライアントを生成する。request は "rq/&lt;name&gt;Request"、
    /// reply は "rr/&lt;name&gt;Reply" に対応する。QoS は ROS 2 services 既定 (Reliable/Volatile)。
    /// </summary>
    public ServiceClient<TRequest, TResponse> CreateServiceClient<TRequest, TResponse>(
        ServiceDescriptor<TRequest, TResponse> descriptor,
        string serviceName)
    {
        ThrowIfDisposed();
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Value cannot be null or empty.", nameof(serviceName));

        var requestPublisher = CreatePublisher(
            TopicNameMangler.DemangleTopic(TopicNameMangler.MangleServiceRequest(serviceName)),
            descriptor.RequestSerializer,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile,
            descriptor.RequestDdsTypeName);

        var replyReader = CreateReliableReplyReaderInternal(
            TopicNameMangler.MangleServiceReply(serviceName),
            descriptor.ResponseDdsTypeName);

        return new ServiceClient<TRequest, TResponse>(
            requestPublisher, replyReader, descriptor, _options.Logger, _options.CdrReadLimits);
    }
```

> 注意: `CreatePublisher` は内部で `TopicNameMangler.MangleTopic`（`rt/` を付与）を呼ぶ。サービス request トピックは `rq/...Request` でなければならないため、`CreatePublisher` をそのまま使うと誤った `rt/` prefix になる。**この問題を避けるため、Step 4 で request writer も内部ヘルパ経由にする。**

- [ ] **Step 4: request writer も DDS トピック名を直接指定できる内部ヘルパにする**

`CreatePublisher` の writer 生成部分（mangle 以外）を `CreateWriterInternal(string ddsTopic, ICdrSerializer<T>, ReliabilityQos, DurabilityQos, string ddsTypeName)` に切り出し、`CreatePublisher` と `CreateServiceClient` の双方から使う。`CreateServiceClient` は `ddsTopic = TopicNameMangler.MangleServiceRequest(serviceName)` を渡す。

`CreateWriterInternal` の骨子（`CreatePublisher` の本体から `MangleTopic` 行を除いたもの）:
```csharp
    private Publisher<T> CreateWriterInternal<T>(
        string ddsTopic,
        ICdrSerializer<T> serializer,
        ReliabilityQos reliability,
        DurabilityQos durability,
        string ddsTypeName,
        string userTopicName)
    {
        var writerEntityId = _userEntityIds.AllocateWriter();
        var writerGuid = new Guid(GuidPrefix, writerEntityId);
        var history = new Rtps.HistoryCache.WriterHistoryCache(writerGuid, maxSamples: _options.UserWriterHistoryDepth);
        var writer = new StatefulWriter(
            sendTransport: _transports.UserUnicast,
            multicastDestination: _transports.UserMulticastDestination,
            version: _options.ProtocolVersion,
            vendorId: _options.VendorId,
            localPrefix: GuidPrefix,
            writerEntityId: writerEntityId,
            heartbeatPeriod: _options.UserWriterHeartbeatPeriod,
            history: history,
            logger: _options.Logger,
            resendHistoryOnMatch: durability.Kind == DurabilityKind.TransientLocal);

        var endpointData = new DiscoveredEndpointData
        {
            Kind = EndpointKind.Writer,
            EndpointGuid = writerGuid,
            ParticipantGuid = Guid,
            TopicName = ddsTopic,
            TypeName = ddsTypeName,
            Reliability = reliability,
            Durability = durability,
        };
        endpointData.UnicastLocators.AddRange(_transports.DefaultUnicastLocators);
        endpointData.MulticastLocators.Add(_transports.UserMulticastDestination);
        _userEndpoints.RegisterWriter(endpointData, writer);
        _ = RunSedpOperationAsync(
            token => _sedpPublicationsWriter.AddEndpointAsync(endpointData, token),
            "DomainParticipant failed to advertise local writer endpoint");

        var pub = new Publisher<T>(userTopicName, writer, serializer, UnregisterLocalWriter);
        pub.Start();
        return pub;
    }
```

`CreatePublisher` の本体を `return CreateWriterInternal(TopicNameMangler.MangleTopic(topicName), serializer, reliability, durability, ResolveDdsTypeName<T>(typeName), topicName);` に置き換える。`CreateServiceClient` の request 生成を以下へ:
```csharp
        var requestPublisher = CreateWriterInternal(
            TopicNameMangler.MangleServiceRequest(serviceName),
            descriptor.RequestSerializer,
            ReliabilityQos.Reliable,
            DurabilityQos.Volatile,
            descriptor.RequestDdsTypeName,
            userTopicName: serviceName);
```
（Step 3 の `CreatePublisher` 経由の request 生成行は削除する。）

- [ ] **Step 5: ビルド確認**

Run: `dotnet build src/rosettadds/rosettadds.csproj`
Expected: 成功。

- [ ] **Step 6: コミット**

```bash
git add src/rosettadds/Dds/ServiceClient.cs src/rosettadds/Dds/ReliableUserReader.cs src/rosettadds/Dds/DomainParticipant.cs
git commit -m "feat(dds): ServiceClient と CreateServiceClient を追加"
```

---

## Phase D: 結合テストとドキュメント

### Task D1: ループバック結合テスト

**Files:**
- Create: `tests/rosettadds.Tests/Integration/ServiceClientLoopbackTests.cs`

既存のループバック結合テスト（`tests/rosettadds.Tests/Integration/PubSubLoopbackTests.cs`）の participant セットアップ方法を参照し、同じ流儀で 2 つの participant を立てる。サーバ役は「rq トピックの Subscription で request を受け、rr トピックへ related_sample_identity 付き reply を publish する」スタブを手で組む。

> 実装前に `PubSubLoopbackTests.cs` を開き、participant 生成・ドメイン分離・待機ヘルパ（`SpinWait`/ポーリング）の作法に合わせること。

- [ ] **Step 1: テストを書く**

サーバ役 reply の related_sample_identity には、request の DataReader が観測した `(request-writer GUID, request writerSN)` を入れる。スタブサーバは rq の Subscription ではなく、reply に inline QoS を載せる必要があるため、`StatefulWriter` を直接使い `BuildDataPackets` 相当ではなく、reply writer に inline QoS を載せる経路が必要になる。**注: 現状の `StatefulWriter` は Alive サンプルに任意 inline QoS を載せられない（status info のみ）。** そのため本テストでは「related_sample_identity を載せた DATA を組み立てて UDP/loopback に直接流す軽量スタブ」を用いる。

テスト方針（擬似コード／実装はリポジトリの既存テストユーティリティに合わせる）:
```csharp
// 1. clientParticipant と serverParticipant を別ドメイン分離なしの loopback で起動
// 2. client = clientParticipant.CreateServiceClient(AddTwoIntsService.Descriptor, "add_two_ints")
//    （テスト用 .srv 生成物が必要。tests プロジェクトに example_interfaces/srv/AddTwoInts.srv を
//      AdditionalFiles 登録して SourceGenerator で生成するか、手書きの記述子を用意する）
// 3. server 役: serverParticipant.CreateSubscription<AddTwoIntsRequest>(
//        "rq/add_two_intsRequest" を Demangle した名前... ではなく、サービス request を購読するには
//        rq トピックを直接 subscribe する必要がある。CreateSubscription は rt/ を付けるため、
//        サーバスタブも CreateServiceServer 相当が無い現状では低レベル reader が要る。
```

**重要な前提整理:** クライアント単独の結合テストを「実 ROS 2 無し」で行うには、related_sample_identity 付き reply を出すサーバ役が必要。これは実質サーバ実装の一部であり、本スペックのスコープ外。したがって **本タスクのループバックテストは、`ServiceClient` の内部結線（publish→相関→deserialize）を、reply reader の `SampleReceived` を直接 fire する単体寄り結合テストで検証する** ことに切り替える。

- [ ] **Step 1（改）: ServiceClient の相関ロジックを検証するテストを書く**

`CreateServiceClient` で client を作り、`client.RequestWriterGuid` を取得。`CallAsync` を起動（await しない）。送信された request の SN を得る手段として、`Publisher.Writer.History.LastSequenceNumber`（`WriterHistoryCache.LastSequenceNumber` は公開済み）で SN を読む。その `(RequestWriterGuid, sn)` で related_sample_identity を作り、reply payload（response をエンキャプ付きシリアライズ）と共に `replyReader` の `SampleReceived` を発火させ、`CallAsync` が正しい response を返すことを確認する。

`SampleReceived` を発火させるには reply reader への参照が要る。テスト容易性のため、`ServiceClient` に `internal void OnReplyForTest(CacheChange change) => OnReplyReceived(change);` を追加するか、`internal` の reply reader を露出する。ここでは `ServiceClient` に internal テストフックを足す:

```csharp
    // ServiceClient.cs に追加
    internal void InjectReplyForTest(CacheChange change) => OnReplyReceived(change);
```

テスト:
```csharp
using ROSettaDDS.Cdr;
using ROSettaDDS.Cdr.ParameterList;
using ROSettaDDS.Common;
using ROSettaDDS.Dds;
using ROSettaDDS.Msgs.ExampleInterfaces; // 生成された AddTwoIntsService / 型
using ROSettaDDS.Rtps.HistoryCache;
using Guid = ROSettaDDS.Common.Guid;

namespace ROSettaDDS.Tests.Integration;

public class ServiceClientLoopbackTests
{
    [Fact]
    public async Task CallAsync_は_related_identity_で_相関した_response_を返す()
    {
        using var participant = TestParticipants.CreateLoopback(); // 既存ヘルパ流儀に合わせる
        participant.Start();
        using var client = participant.CreateServiceClient(AddTwoIntsService.Descriptor, "add_two_ints");

        var callTask = client.CallAsync(new AddTwoInts_Request(2, 3), TimeSpan.FromSeconds(2));

        // 送信された request の SN を取得して相関キーを作る
        await WaitUntil(() => RequestSnIsAssigned(participant));
        var sn = LastRequestSn(participant);
        var related = new SampleIdentity(client.RequestWriterGuid, sn);

        // response をエンキャプ付きでシリアライズ
        var response = new AddTwoInts_Response(5);
        var payload = SerializeResponse(response);
        var inlineQos = RelatedSampleIdentityInlineQos.Build(related, CdrEndianness.LittleEndian);
        var change = new CacheChange(
            ChangeKind.Alive, client.RequestWriterGuid, sn, Time.Now(), payload, inlineQos, CdrEndianness.LittleEndian);

        client.InjectReplyForTest(change);

        var result = await callTask;
        result.Sum.Should().Be(5);
    }
}
```

ヘルパ（SN 取得・SerializeResponse・WaitUntil）はテストファイル内に実装する。SN は `participant` から request writer の history へ辿るのが難しければ、`ServiceClient` に `internal SequenceNumber LastSentSequenceNumber` を足して公開する。`SerializeResponse` は `AddTwoInts_ResponseSerializer.Instance` + `CdrEncapsulation.Write` で組む（`Publisher.SerializeWithEncapsulation` と同じ手順）。

> tests プロジェクトで `AddTwoIntsService` を使うため、`example_interfaces/srv/AddTwoInts.srv`（`int64 a\nint64 b\n---\nint64 sum`）を tests の AdditionalFiles に登録して SourceGenerator で生成する。登録方法は既存のカスタム msg テスト（`Ros2Unity` や `samples/CustomMsgGen`）の csproj 設定を参照する。

- [ ] **Step 2: テスト通過を確認**

Run: `dotnet test --filter ServiceClientLoopbackTests`
Expected: PASS

- [ ] **Step 3: 全テスト確認**

Run: `dotnet test`
Expected: 全 PASS。

- [ ] **Step 4: コミット**

```bash
git add tests/rosettadds.Tests/Integration/ServiceClientLoopbackTests.cs src/rosettadds/Dds/ServiceClient.cs tests/rosettadds.Tests/rosettadds.Tests.csproj
git commit -m "test(dds): ServiceClient の相関ロジックの結合テストを追加"
```

---

### Task D2: interop ドキュメント更新

**Files:**
- Modify: `docs/interop.md`

- [ ] **Step 1: サービス検証手順を追記**

`docs/interop.md` に、実 ROS 2 (Fast DDS) の `example_interfaces` サーバに対する手動検証手順を追記する。

```markdown
## サービスクライアントの相互運用確認 (例: example_interfaces/AddTwoInts)

ROS 2 側でサーバを起動:

```sh
ros2 run examples_rclpy_minimal_service service
# または
ros2 run examples_rclcpp_minimal_service service_main
```

ROSettaDDS 側から呼び出すサンプルを実行し、`sum` が返ることを確認する:

```csharp
using var participant = new DomainParticipant(new DomainParticipantOptions { DomainId = 0, EntityName = "rosettadds_svc" });
participant.Start();
using var client = participant.CreateServiceClient(AddTwoIntsService.Descriptor, "add_two_ints");
if (!await client.WaitForServiceAsync(TimeSpan.FromSeconds(5)))
{
    Console.Error.WriteLine("service not available");
    return;
}
var resp = await client.CallAsync(new AddTwoInts_Request(2, 3), TimeSpan.FromSeconds(3));
Console.WriteLine($"2 + 3 = {resp.Sum}");
```
```

- [ ] **Step 2: コミット**

```bash
git add docs/interop.md
git commit -m "docs: サービスクライアントの interop 検証手順を追記"
```

---

## 完了後

全タスク完了後:

- [ ] `dotnet build rosettadds.sln` が成功する
- [ ] `dotnet test` が全 PASS
- [ ] `superpowers:finishing-a-development-branch` で PR 作成へ進む

---

## 自己レビューメモ（計画作成者による確認結果）

- **spec カバレッジ**: `.srv` 生成 (A1-A6) / SampleIdentity・inline QoS (B1-B3) / writerSN 返却・reader inline QoS 経路 (C1,C3,C4) / トピック・型 mangle (A2,C5) / ServiceClient API (C2,C6,C7) / テスト (D1) / interop (D2) — spec 各節に対応タスクあり。
- **既知の制約**: D1 のループバックは「実サーバ無し」前提のため、related_sample_identity 付き reply を出すサーバ役を本物では用意できない。よって ServiceClient の相関・deserialize を `InjectReplyForTest` で検証する単体寄り結合に切り替えた。end-to-end の実 ROS 2 検証は D2 の手動手順で担保する。サーバ実装（後続スペック）完成後に真のループバック e2e を追加すること。
- **型整合**: `ServiceDescriptor<TReq,TResp>`（C2）の ctor 引数順 = (requestDdsTypeName, responseDdsTypeName, requestSerializer, responseSerializer) は A4 の生成コードと一致。`MatchedWriterCount`（C7 step2）/ `WriteReturningSequenceNumberAsync`（C3）/ `SampleReceived`（C4）/ `CreateReliableReplyReaderInternal`・`CreateWriterInternal`（C6,C7）の名前は各参照箇所で一致させている。
- **要実装時確認**: `Common/Guid.cs` の `WriteTo`/`Read` シグネチャ（B1）、`VendorId` 定数名（B1）、tests の AdditionalFiles 設定方法（D1）。各タスク内に確認指示を明記済み。
