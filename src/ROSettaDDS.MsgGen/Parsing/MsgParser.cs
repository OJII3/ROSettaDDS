using System;
using System.Collections.Generic;
using ROSettaDDS.MsgGen.Model;
using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.MsgGen.Parsing;

/// <summary>
/// ROS 2 の <c>.msg</c> ファイルを <see cref="MessageDefinition"/> に変換するパーサ。
///
/// サポート構文:
/// <list type="bullet">
/// <item>コメント (<c>#</c> 以降を無視)、空行</item>
/// <item>プリミティブ (bool, byte, char, int8..uint64, float32/64, string)</item>
/// <item>bounded string (<c>string&lt;=N</c>)</item>
/// <item>ネスト型 (<c>pkg/Type</c> または同一パッケージ相対 <c>Type</c>)</item>
/// <item>配列: 固定長 <c>T[N]</c> / 可変長 <c>T[]</c> / 上限付き <c>T[&lt;=N]</c></item>
/// <item>定数 (<c>TYPE NAME=value</c>)</item>
/// <item>デフォルト値 (フィールド名の後ろの値; 文字列はクォート)</item>
/// </list>
/// </summary>
public static class MsgParser
{
    /// <summary>
    /// .msg テキストを解析する。
    /// </summary>
    /// <param name="package">所属パッケージ名 (相対型参照とデフォルト namespace に使う)。</param>
    /// <param name="messageName">メッセージ名 (ファイル名のステム)。</param>
    /// <param name="text">.msg の中身。</param>
    public static MessageDefinition Parse(string package, string messageName, string text)
    {
        if (string.IsNullOrEmpty(package)) throw new ArgumentException("package is required", nameof(package));
        if (string.IsNullOrEmpty(messageName)) throw new ArgumentException("messageName is required", nameof(messageName));

        var constants = new List<MessageConstant>();
        var fields = new List<MessageField>();

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int lineNo = i + 1;

            // "TYPE rest" に分割
            int sp = IndexOfWhitespace(line);
            if (sp < 0)
            {
                throw new MsgParseException($"{messageName}.msg:{lineNo}: 型と名前を判別できません: '{line}'");
            }

            string typeToken = line.Substring(0, sp);
            string rest = line.Substring(sp).Trim();

            FieldType type = ParseType(typeToken, package, messageName, lineNo);

            // 定数か (名前トークンに '=' が含まれる) フィールドか
            int eq = IndexOfTopLevelEquals(rest);
            if (eq >= 0)
            {
                string name = rest.Substring(0, eq).Trim();
                string value = rest.Substring(eq + 1).Trim();
                if (name.Length == 0)
                {
                    throw new MsgParseException($"{messageName}.msg:{lineNo}: 定数名が空です");
                }
                constants.Add(new MessageConstant(type, name, value));
            }
            else
            {
                // "NAME [default]"
                int sp2 = IndexOfWhitespace(rest);
                string name;
                string? def = null;
                if (sp2 < 0)
                {
                    name = rest;
                }
                else
                {
                    name = rest.Substring(0, sp2);
                    def = rest.Substring(sp2).Trim();
                    if (def.Length == 0)
                    {
                        def = null;
                    }
                }
                fields.Add(new MessageField(type, name, def));
            }
        }

        return new MessageDefinition(package, messageName, constants, fields);
    }

    private static FieldType ParseType(string token, string package, string messageName, int lineNo)
    {
        // 配列サフィックスを切り出す: "...[...]" or "...[]"
        ArrayKind arrayKind = ArrayKind.None;
        int arrayLength = 0;
        string baseToken = token;

        int lb = token.IndexOf('[');
        if (lb >= 0)
        {
            if (!token.EndsWith("]", StringComparison.Ordinal))
            {
                throw new MsgParseException($"{messageName}.msg:{lineNo}: 配列指定が不正です: '{token}'");
            }
            baseToken = token.Substring(0, lb);
            string inner = token.Substring(lb + 1, token.Length - lb - 2).Trim();
            if (inner.Length == 0)
            {
                arrayKind = ArrayKind.Unbounded;
            }
            else if (inner.StartsWith("<=", StringComparison.Ordinal))
            {
                arrayKind = ArrayKind.Bounded;
                arrayLength = ParseLength(inner.Substring(2).Trim(), messageName, lineNo);
            }
            else
            {
                arrayKind = ArrayKind.FixedSize;
                arrayLength = ParseLength(inner, messageName, lineNo);
            }
        }

        // bounded string: "string<=N"
        int? stringBound = null;
        int le = baseToken.IndexOf("<=", StringComparison.Ordinal);
        if (le >= 0)
        {
            string head = baseToken.Substring(0, le);
            if (head != "string" && head != "wstring")
            {
                throw new MsgParseException($"{messageName}.msg:{lineNo}: '<=' は string にのみ使えます: '{baseToken}'");
            }
            stringBound = ParseLength(baseToken.Substring(le + 2).Trim(), messageName, lineNo);
            baseToken = head;
        }

        // wstring は CDR 上 UTF-16 で string (UTF-8) と wire が異なるため未対応。
        if (baseToken == "wstring")
        {
            throw new MsgParseException($"{messageName}.msg:{lineNo}: wstring は未対応です");
        }

        if (IsStringType(baseToken))
        {
            return new FieldType(BaseTypeCategory.String, null, null, null, stringBound, arrayKind, arrayLength);
        }

        if (PrimitiveTypes.IsPrimitive(baseToken))
        {
            return new FieldType(BaseTypeCategory.Primitive, baseToken, null, null, null, arrayKind, arrayLength);
        }

        // ネスト型: "pkg/Type" or "Type" (同一パッケージ)
        int slash = baseToken.IndexOf('/');
        string? pkg;
        string typeName;
        if (slash >= 0)
        {
            pkg = baseToken.Substring(0, slash);
            typeName = baseToken.Substring(slash + 1);
            // "pkg/msg/Type" 形式も許容
            int slash2 = typeName.IndexOf('/');
            if (slash2 >= 0)
            {
                typeName = typeName.Substring(slash2 + 1);
            }
        }
        else
        {
            pkg = null; // 同一パッケージ相対
            typeName = baseToken;
        }

        if (typeName.Length == 0)
        {
            throw new MsgParseException($"{messageName}.msg:{lineNo}: 型名が空です: '{baseToken}'");
        }

        return new FieldType(BaseTypeCategory.Named, null, pkg, typeName, null, arrayKind, arrayLength);
    }

    private static bool IsStringType(string token) => token == "string";

    private static int ParseLength(string s, string messageName, int lineNo)
    {
        if (!int.TryParse(s, out int n) || n < 0)
        {
            throw new MsgParseException($"{messageName}.msg:{lineNo}: 長さが不正です: '{s}'");
        }
        return n;
    }

    /// <summary>'#' 以降を除去する。ただしクォート内の '#' は保持する。</summary>
    private static string StripComment(string line)
    {
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble) return line.Substring(0, i);
        }
        return line;
    }

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) return i;
        }
        return -1;
    }

    /// <summary>クォート外の最初の '=' の位置。なければ -1。</summary>
    private static int IndexOfTopLevelEquals(string s)
    {
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '=' && !inSingle && !inDouble) return i;
        }
        return -1;
    }
}
