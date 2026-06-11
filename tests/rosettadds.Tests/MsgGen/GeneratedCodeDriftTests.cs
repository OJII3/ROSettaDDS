using System.IO;
using System.Text;
using ROSettaDDS.MsgGen.Emitting;
using ROSettaDDS.MsgGen.Model;
using ROSettaDDS.MsgGen.Parsing;
using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.Tests.MsgGen;

/// <summary>
/// <c>msgs/</c> を再生成した結果が、コミット済みの <c>src/rosettadds/Msgs/</c> と
/// 一致することを検証する。生成器の出力が冪等であり、かつ手書き編集で
/// ドリフトしていないことを保証する。
/// </summary>
public class GeneratedCodeDriftTests
{
    [Fact]
    public void 生成コードはコミット済みと一致する()
    {
        string root = RepoRoot();
        string msgsDir = Path.Combine(root, "msgs");
        var resolver = new TypeNameResolver();

        // CLI と同じく全 .msg を解析してレジストリを構築してから生成する。
        var parsed = new List<(string package, string name, MessageDefinition def)>();
        foreach (var file in Directory.GetFiles(msgsDir, "*.msg", SearchOption.AllDirectories))
        {
            string msgDir = Path.GetDirectoryName(file)!;
            string package = Path.GetFileName(Path.GetDirectoryName(msgDir)!);
            string name = Path.GetFileNameWithoutExtension(file);
            parsed.Add((package, name, MsgParser.Parse(package, name, File.ReadAllText(file))));
        }

        var registry = new MessageRegistry(parsed.ConvertAll(p => p.def));
        var emitter = new CSharpEmitter(resolver, registry);

        var drifted = new StringBuilder();
        foreach (var (package, name, def) in parsed)
        {
            string generated = emitter.Emit(def);
            string committedPath = Path.Combine(
                root, "src", "rosettadds", "Msgs",
                resolver.SubNamespace(package),
                resolver.CSharpTypeName(package, name) + ".cs");

            string? committed = File.Exists(committedPath) ? File.ReadAllText(committedPath) : null;
            if (!string.Equals(committed, generated, StringComparison.Ordinal))
            {
                drifted.AppendLine(committedPath);
            }
        }

        drifted.ToString().Should().BeEmpty(
            "生成結果がコミット済みと一致するべき (rosettadds-genmsg を再実行してコミットしてください)");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rosettadds.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new DirectoryNotFoundException("rosettadds.sln が見つかりません");
        return dir.FullName;
    }
}
