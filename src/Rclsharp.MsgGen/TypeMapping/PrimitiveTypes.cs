using System.Collections.Generic;

namespace Rclsharp.MsgGen.TypeMapping;

/// <summary>1 つの ROS プリミティブ型に対応する C# 表現と CDR 入出力情報。</summary>
public sealed class PrimitiveInfo
{
    /// <summary>C# 型名 (例 "int", "byte")。</summary>
    public string CSharpType { get; }

    /// <summary>CdrWriter のメソッド名 (例 "WriteInt32")。</summary>
    public string WriteMethod { get; }

    /// <summary>CdrReader のメソッド名 (例 "ReadInt32")。</summary>
    public string ReadMethod { get; }

    /// <summary>1 要素のバイトサイズ (= CDR アライメント)。</summary>
    public int Size { get; }

    public PrimitiveInfo(string cSharpType, string writeMethod, string readMethod, int size)
    {
        CSharpType = cSharpType;
        WriteMethod = writeMethod;
        ReadMethod = readMethod;
        Size = size;
    }
}

/// <summary>
/// ROS プリミティブ型 ⇔ C#/CDR の対応表。
/// 既存の手書き <c>Rclsharp.Msgs.*</c> の型選択 (char/int8→sbyte, byte/uint8→byte) に一致させる。
/// </summary>
public static class PrimitiveTypes
{
    private static readonly Dictionary<string, PrimitiveInfo> Map = new()
    {
        ["bool"] = new PrimitiveInfo("bool", "WriteBool", "ReadBool", 1),
        ["byte"] = new PrimitiveInfo("byte", "WriteByte", "ReadByte", 1),
        ["char"] = new PrimitiveInfo("sbyte", "WriteSByte", "ReadSByte", 1),
        ["int8"] = new PrimitiveInfo("sbyte", "WriteSByte", "ReadSByte", 1),
        ["uint8"] = new PrimitiveInfo("byte", "WriteByte", "ReadByte", 1),
        ["int16"] = new PrimitiveInfo("short", "WriteInt16", "ReadInt16", 2),
        ["uint16"] = new PrimitiveInfo("ushort", "WriteUInt16", "ReadUInt16", 2),
        ["int32"] = new PrimitiveInfo("int", "WriteInt32", "ReadInt32", 4),
        ["uint32"] = new PrimitiveInfo("uint", "WriteUInt32", "ReadUInt32", 4),
        ["int64"] = new PrimitiveInfo("long", "WriteInt64", "ReadInt64", 8),
        ["uint64"] = new PrimitiveInfo("ulong", "WriteUInt64", "ReadUInt64", 8),
        ["float32"] = new PrimitiveInfo("float", "WriteFloat", "ReadFloat", 4),
        ["float64"] = new PrimitiveInfo("double", "WriteDouble", "ReadDouble", 8),
    };

    public static bool IsPrimitive(string rosName) => Map.ContainsKey(rosName);

    public static PrimitiveInfo Get(string rosName) => Map[rosName];
}
