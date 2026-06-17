using ROSettaDDS.MsgGen.Emitting;
using ROSettaDDS.MsgGen.Parsing;
using ROSettaDDS.MsgGen.TypeMapping;

// rosettadds-genmsg: ROS 2 の .msg から C# (struct + ICdrSerializer) を生成する CLI。
//
// Usage:
//   rosettadds-genmsg --input <msgsDir> --output <MsgsCsDir> [--check]
//
//   --input   パッケージ別 .msg を含むルート (例: msgs/)。各ファイルは
//             <input>/<package>/msg/<Name>.msg のレイアウトを想定。
//   --output  生成 .cs の出力先 (例: src/rosettadds/Msgs/)。
//             <output>/<SubNamespace>/<TypeName>.cs に書き出す。
//   --check   ファイルを書き換えず、既存と差分があれば非ゼロ終了 (CI 用)。

string? input = null;
string? output = null;
bool check = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input":
            if (++i >= args.Length) { Console.Error.WriteLine("--input requires a value"); return 2; }
            input = args[i];
            break;
        case "--output":
            if (++i >= args.Length) { Console.Error.WriteLine("--output requires a value"); return 2; }
            output = args[i];
            break;
        case "--check": check = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (input is null || output is null)
{
    Console.Error.WriteLine("Usage: rosettadds-genmsg --input <msgsDir> --output <MsgsCsDir> [--check]");
    return 2;
}

input = Path.GetFullPath(input);
output = Path.GetFullPath(output);

if (!Directory.Exists(input))
{
    Console.Error.WriteLine($"Input directory not found: {input}");
    return 2;
}

var resolver = new TypeNameResolver();

var msgFiles = Directory.GetFiles(input, "*.msg", SearchOption.AllDirectories);
Array.Sort(msgFiles, StringComparer.Ordinal);

// 1st pass: 全 .msg を解析してレジストリを構築 (ネスト型のサイズ/整列を正確に解決するため)。
var parsed = new List<ROSettaDDS.MsgGen.Model.MessageDefinition>();
foreach (var file in msgFiles)
{
    // <input>/<package>/msg/<Name>.msg を想定。
    string? msgDir = Path.GetDirectoryName(file);
    string? pkgDir = msgDir is null ? null : Path.GetDirectoryName(msgDir);
    if (msgDir is null || pkgDir is null || !Path.GetFileName(msgDir).Equals("msg", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Skip (expected <package>/msg/<Name>.msg layout): {file}");
        continue;
    }

    string package = Path.GetFileName(pkgDir);
    string name = Path.GetFileNameWithoutExtension(file);
    parsed.Add(MsgParser.Parse(package, name, File.ReadAllText(file)));
}

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

var registry = new ROSettaDDS.MsgGen.Model.MessageRegistry(parsed);
var emitter = new CSharpEmitter(resolver, registry);

int changed = 0;
int total = 0;

// 2nd pass: 生成して出力。
foreach (var def in parsed)
{
    string package = def.Package;
    string name = def.Name;
    string code = emitter.Emit(def);

    string subNs = resolver.SubNamespace(package);
    string typeName = resolver.CSharpTypeName(package, name);
    string outDir = Path.Combine(output, subNs);
    string outPath = Path.Combine(outDir, typeName + ".cs");

    total++;

    string? existing = File.Exists(outPath) ? File.ReadAllText(outPath) : null;
    bool differs = !string.Equals(existing, code, StringComparison.Ordinal);

    if (check)
    {
        if (differs)
        {
            changed++;
            Console.Error.WriteLine($"DRIFT: {outPath}");
        }
        continue;
    }

    if (differs)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(outPath, code);
        changed++;
        Console.WriteLine($"generated: {outPath}");
    }
}

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

if (check)
{
    Console.WriteLine($"checked {total} message(s), {changed} drifted");
    return changed == 0 ? 0 : 1;
}

Console.WriteLine($"generated {total} message(s), {changed} written");
return 0;
