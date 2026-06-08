using Rclsharp.MsgGen.Emitting;
using Rclsharp.MsgGen.Parsing;
using Rclsharp.MsgGen.TypeMapping;

// rclsharp-genmsg: ROS 2 の .msg から C# (struct + ICdrSerializer) を生成する CLI。
//
// Usage:
//   rclsharp-genmsg --input <msgsDir> --output <MsgsCsDir> [--check]
//
//   --input   パッケージ別 .msg を含むルート (例: msgs/)。各ファイルは
//             <input>/<package>/msg/<Name>.msg のレイアウトを想定。
//   --output  生成 .cs の出力先 (例: src/rclsharp/Msgs/)。
//             <output>/<SubNamespace>/<TypeName>.cs に書き出す。
//   --check   ファイルを書き換えず、既存と差分があれば非ゼロ終了 (CI 用)。

string? input = null;
string? output = null;
bool check = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input": input = args[++i]; break;
        case "--output": output = args[++i]; break;
        case "--check": check = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (input is null || output is null)
{
    Console.Error.WriteLine("Usage: rclsharp-genmsg --input <msgsDir> --output <MsgsCsDir> [--check]");
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
var emitter = new CSharpEmitter(resolver);

var msgFiles = Directory.GetFiles(input, "*.msg", SearchOption.AllDirectories);
Array.Sort(msgFiles, StringComparer.Ordinal);

int changed = 0;
int total = 0;

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
    string text = File.ReadAllText(file);

    var def = MsgParser.Parse(package, name, text);
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

if (check)
{
    Console.WriteLine($"checked {total} message(s), {changed} drifted");
    return changed == 0 ? 0 : 1;
}

Console.WriteLine($"generated {total} message(s), {changed} written");
return 0;
