using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ROSettaDDS.MsgGen.Emitting;
using ROSettaDDS.MsgGen.Model;
using ROSettaDDS.MsgGen.Parsing;
using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.SourceGenerator;

/// <summary>
/// <c>AdditionalFiles</c> に登録された <c>.msg</c> を、コンパイル時に
/// <c>struct</c> + <c>ICdrSerializer&lt;T&gt;</c> へ変換するインクリメンタル生成器。
///
/// パッケージ名の決定:
/// <list type="number">
/// <item>AdditionalFiles メタデータ <c>ROSettaDDSMsgPackage</c> があればそれを使う。</item>
/// <item>無ければパス <c>.../&lt;package&gt;/msg/&lt;Name&gt;.msg</c> から推定する。</item>
/// </list>
/// メタデータを使う場合、消費側 csproj で
/// <c>&lt;CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="ROSettaDDSMsgPackage" /&gt;</c>
/// を宣言する必要がある。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MsgSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ParseError = new(
        id: "RCLMSG001",
        title: "msg の解析に失敗しました",
        messageFormat: "{0}",
        category: "ROSettaDDS.MsgGen",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(static a => a.Path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, ct) =>
            {
                AdditionalText file = pair.Left;
                var options = pair.Right.GetOptions(file);
                options.TryGetValue("build_metadata.AdditionalFiles.ROSettaDDSMsgPackage", out string? package);
                string text = file.GetText(ct)?.ToString() ?? string.Empty;
                return new MsgInput(file.Path, package, text);
            })
            .Collect();

        // 全 .msg をまとめて処理し、ネスト型のサイズ/整列を正確に解決するためのレジストリを構築する。
        context.RegisterSourceOutput(inputs, GenerateAll);
    }

    private static void GenerateAll(SourceProductionContext context, ImmutableArray<MsgInput> inputs)
    {
        var resolver = new TypeNameResolver();
        var parsed = new List<(MsgInput input, string package, string name, MessageDefinition def)>();

        foreach (var input in inputs)
        {
            string name = Path.GetFileNameWithoutExtension(input.Path);
            string package = !string.IsNullOrEmpty(input.Package) ? input.Package! : InferPackage(input.Path);
            try
            {
                var def = MsgParser.Parse(package, name, input.Text);
                parsed.Add((input, package, name, def));
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, ex.Message));
            }
        }

        var registry = new MessageRegistry();
        foreach (var p in parsed) registry.Add(p.def);
        var emitter = new CSharpEmitter(resolver, registry);

        foreach (var p in parsed)
        {
            try
            {
                string code = emitter.Emit(p.def);
                string hint = $"{p.package}_{resolver.CSharpTypeName(p.package, p.name)}.g.cs";
                context.AddSource(hint, SourceText.From(code, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, ex.Message));
            }
        }
    }

    /// <summary>パス <c>.../&lt;package&gt;/msg/&lt;Name&gt;.msg</c> からパッケージ名を推定する。</summary>
    private static string InferPackage(string path)
    {
        string? msgDir = Path.GetDirectoryName(path);
        string? parent = msgDir is null ? null : Path.GetDirectoryName(msgDir);
        if (msgDir is not null && parent is not null &&
            string.Equals(Path.GetFileName(msgDir), "msg", StringComparison.Ordinal))
        {
            string pkg = Path.GetFileName(parent);
            if (pkg.Length > 0) return pkg;
        }
        // フォールバック: 直近のディレクトリ名。
        return msgDir is null ? "msgs" : Path.GetFileName(msgDir);
    }

    private readonly struct MsgInput : IEquatable<MsgInput>
    {
        public readonly string Path;
        public readonly string? Package;
        public readonly string Text;

        public MsgInput(string path, string? package, string text)
        {
            Path = path;
            Package = package;
            Text = text;
        }

        public bool Equals(MsgInput other) =>
            Path == other.Path && Package == other.Package && Text == other.Text;

        public override bool Equals(object? obj) => obj is MsgInput o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Path.GetHashCode();
                h = (h * 397) ^ (Package?.GetHashCode() ?? 0);
                h = (h * 397) ^ Text.GetHashCode();
                return h;
            }
        }
    }
}
