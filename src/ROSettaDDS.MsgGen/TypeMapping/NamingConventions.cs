using System.Collections.Generic;
using System.Text;

namespace ROSettaDDS.MsgGen.TypeMapping;

/// <summary>
/// ROS の snake_case / 型名を C# 慣習へ変換する。
/// 頭字語の連続 (例 "ColorRGBA") は "ColorRgba" に正規化する。
/// </summary>
public static class NamingConventions
{
    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>snake_case や PascalCase の識別子を PascalCase に正規化する。</summary>
    public static string ToPascalCase(string identifier)
    {
        var sb = new StringBuilder(identifier.Length);
        foreach (string word in identifier.Split('_'))
        {
            if (word.Length == 0) continue;
            sb.Append(NormalizeWord(word));
        }
        if (sb.Length == 0) return identifier;
        if (char.IsLower(sb[0])) sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    /// <summary>PascalCase にした上で先頭を小文字化し、C# 予約語なら '@' を付ける。</summary>
    public static string ToCamelCase(string identifier)
    {
        string pascal = ToPascalCase(identifier);
        if (pascal.Length == 0) return identifier;
        var chars = pascal.ToCharArray();
        chars[0] = char.ToLowerInvariant(chars[0]);
        string camel = new string(chars);
        return CSharpKeywords.Contains(camel) ? "@" + camel : camel;
    }

    /// <summary>1 単語内の頭字語連続を正規化 ("RGBA" → "Rgba")。</summary>
    private static string NormalizeWord(string word)
    {
        var sb = new StringBuilder(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (!char.IsUpper(c))
            {
                sb.Append(c);
                continue;
            }
            bool prevUpper = i > 0 && char.IsUpper(word[i - 1]);
            bool nextLower = i + 1 < word.Length && char.IsLower(word[i + 1]);
            // 頭字語連続の途中 (前が大文字、次が小文字でない) は小文字化する。
            sb.Append(prevUpper && !nextLower ? char.ToLowerInvariant(c) : c);
        }
        if (sb.Length > 0 && char.IsLower(sb[0]))
        {
            sb[0] = char.ToUpperInvariant(sb[0]);
        }
        return sb.ToString();
    }
}
