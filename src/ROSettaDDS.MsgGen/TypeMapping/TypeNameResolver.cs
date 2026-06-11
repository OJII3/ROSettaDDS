using System.Collections.Generic;

namespace ROSettaDDS.MsgGen.TypeMapping;

/// <summary>
/// ROS パッケージ/型名から C# の名前空間・型名・シリアライザ名・DDS 型名を決める。
///
/// 既存の手書き <c>ROSettaDDS.Msgs.*</c> と一致させるためのオーバライドを持つ:
/// <list type="bullet">
/// <item>BCL と衝突する std_msgs のスカラ型は <c>Message</c> サフィックス付き
/// (案 B / .claude/CLAUDE.md)。</item>
/// <item>名前空間は <c>std_msgs → ROSettaDDS.Msgs.Std</c> のように <c>_msgs</c> を畳む。</item>
/// </list>
/// </summary>
public sealed class TypeNameResolver
{
    public const string RootNamespace = "ROSettaDDS.Msgs";

    /// <summary><c>Message</c> サフィックスを付ける ROS 型 ("package/Name")。</summary>
    private static readonly HashSet<string> MessageSuffixTypes = new()
    {
        "std_msgs/Bool", "std_msgs/Byte", "std_msgs/Char", "std_msgs/Empty", "std_msgs/String",
        "std_msgs/Float32", "std_msgs/Float64",
        "std_msgs/Int8", "std_msgs/Int16", "std_msgs/Int32", "std_msgs/Int64",
        "std_msgs/UInt8", "std_msgs/UInt16", "std_msgs/UInt32", "std_msgs/UInt64",
    };

    /// <summary>パッケージ名 → サブ名前空間の明示マッピング (アルゴリズムで決まらないもの)。</summary>
    private static readonly Dictionary<string, string> NamespaceOverrides = new()
    {
        ["builtin_interfaces"] = "BuiltinInterfaces",
    };

    /// <summary>C# 型名 (例 "StringMessage", "ColorRgba", "Header")。</summary>
    public string CSharpTypeName(string package, string rosName)
    {
        string baseName = NamingConventions.ToPascalCase(rosName);
        return MessageSuffixTypes.Contains($"{package}/{rosName}") ? baseName + "Message" : baseName;
    }

    /// <summary>シリアライザ型名 (型名 + "Serializer")。</summary>
    public string SerializerName(string package, string rosName) => CSharpTypeName(package, rosName) + "Serializer";

    /// <summary>サブ名前空間 (例 "Std", "BuiltinInterfaces")。</summary>
    public string SubNamespace(string package)
    {
        if (NamespaceOverrides.TryGetValue(package, out string? overridden))
        {
            return overridden;
        }
        // "std_msgs" → "std" → "Std"、"geometry_msgs" → "Geometry"
        string trimmed = package.EndsWith("_msgs") ? package.Substring(0, package.Length - "_msgs".Length) : package;
        return NamingConventions.ToPascalCase(trimmed);
    }

    /// <summary>完全な名前空間 (例 "ROSettaDDS.Msgs.Std")。</summary>
    public string Namespace(string package) => $"{RootNamespace}.{SubNamespace(package)}";

    /// <summary>名前空間付きの C# 型名 (例 "ROSettaDDS.Msgs.Std.Header")。</summary>
    public string FullyQualifiedName(string package, string rosName) =>
        $"{Namespace(package)}.{CSharpTypeName(package, rosName)}";

    /// <summary>ROS 2 型名 (例 "std_msgs/msg/Header")。</summary>
    public string RosTypeName(string package, string rosName) => $"{package}/msg/{rosName}";

    /// <summary>DDS 型名 (例 "std_msgs::msg::dds_::Header_")。</summary>
    public string DdsTypeName(string package, string rosName) => $"{package}::msg::dds_::{rosName}_";
}
