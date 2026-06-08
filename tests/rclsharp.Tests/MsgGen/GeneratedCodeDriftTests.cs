using System.IO;
using Rclsharp.MsgGen.Emitting;
using Rclsharp.MsgGen.Parsing;
using Rclsharp.MsgGen.TypeMapping;

namespace Rclsharp.Tests.MsgGen;

/// <summary>
/// <c>msgs/</c> を再生成した結果が、コミット済みの <c>src/rclsharp/Msgs/</c> と
/// 一致することを検証する。生成器の出力が冪等であり、かつ手書き編集で
/// ドリフトしていないことを保証する。
/// </summary>
public class GeneratedCodeDriftTests
{
    public static IEnumerable<object[]> MsgFiles()
    {
        string root = RepoRoot();
        string msgsDir = Path.Combine(root, "msgs");
        foreach (var file in Directory.GetFiles(msgsDir, "*.msg", SearchOption.AllDirectories))
        {
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(MsgFiles))]
    public void 生成コードはコミット済みと一致する(string msgFile)
    {
        string root = RepoRoot();
        var resolver = new TypeNameResolver();
        var emitter = new CSharpEmitter(resolver);

        string msgDir = Path.GetDirectoryName(msgFile)!;
        string package = Path.GetFileName(Path.GetDirectoryName(msgDir)!);
        string name = Path.GetFileNameWithoutExtension(msgFile);

        var def = MsgParser.Parse(package, name, File.ReadAllText(msgFile));
        string generated = emitter.Emit(def);

        string committedPath = Path.Combine(
            root, "src", "rclsharp", "Msgs",
            resolver.SubNamespace(package),
            resolver.CSharpTypeName(package, name) + ".cs");

        File.Exists(committedPath).Should().BeTrue($"{committedPath} が存在するはず");
        string committed = File.ReadAllText(committedPath);

        generated.Should().Be(
            committed,
            $"{name}.msg の生成結果が {committedPath} と一致するべき (rclsharp-genmsg を再実行してコミットしてください)");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rclsharp.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new DirectoryNotFoundException("rclsharp.sln が見つかりません");
        return dir.FullName;
    }
}
