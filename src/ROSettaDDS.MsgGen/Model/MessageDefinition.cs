using System.Collections.Generic;

namespace ROSettaDDS.MsgGen.Model;

/// <summary>.msg の 1 フィールド。</summary>
public sealed class MessageField
{
    public FieldType Type { get; }

    /// <summary>ROS 上のフィールド名 (snake_case)。</summary>
    public string Name { get; }

    /// <summary>デフォルト値リテラル (ソース上の生表現)。無指定なら null。</summary>
    public string? DefaultValue { get; }

    public MessageField(FieldType type, string name, string? defaultValue)
    {
        Type = type;
        Name = name;
        DefaultValue = defaultValue;
    }
}

/// <summary>.msg の定数定義 (<c>int32 X=1</c>)。</summary>
public sealed class MessageConstant
{
    public FieldType Type { get; }

    /// <summary>定数名 (慣習上 UPPER_CASE)。</summary>
    public string Name { get; }

    /// <summary>定数値リテラル (ソース上の生表現)。</summary>
    public string Value { get; }

    public MessageConstant(FieldType type, string name, string value)
    {
        Type = type;
        Name = name;
        Value = value;
    }
}

/// <summary>
/// 1 つの .msg ファイルを表す解析済みモデル。
/// </summary>
public sealed class MessageDefinition
{
    /// <summary>パッケージ名 (例 "std_msgs")。</summary>
    public string Package { get; }

    /// <summary>ROS メッセージ名 (例 "Header", "String")。</summary>
    public string Name { get; }

    public IReadOnlyList<MessageConstant> Constants { get; }

    public IReadOnlyList<MessageField> Fields { get; }

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
}
