namespace Rclsharp.MsgGen.Model;

/// <summary>フィールドの基底型カテゴリ。</summary>
public enum BaseTypeCategory
{
    /// <summary>数値・真偽など固定長プリミティブ (bool, int32, float64 など)。</summary>
    Primitive,

    /// <summary>可変長文字列 (string)。</summary>
    String,

    /// <summary>他の msg 型 (例: std_msgs/Header)。</summary>
    Named,
}

/// <summary>配列の種類。</summary>
public enum ArrayKind
{
    /// <summary>配列ではない単一値。</summary>
    None,

    /// <summary>固定長配列 <c>T[N]</c>。長さプレフィックスなし。</summary>
    FixedSize,

    /// <summary>可変長シーケンス <c>T[]</c>。</summary>
    Unbounded,

    /// <summary>上限付きシーケンス <c>T[&lt;=N]</c>。wire 上は Unbounded と同形。</summary>
    Bounded,
}

/// <summary>
/// .msg フィールドの型。基底型 (プリミティブ / string / 他 msg 型) と配列指定を保持する。
/// </summary>
public sealed class FieldType
{
    public BaseTypeCategory Category { get; }

    /// <summary><see cref="BaseTypeCategory.Primitive"/> のとき ROS プリミティブ名 (例 "int32")。</summary>
    public string? PrimitiveName { get; }

    /// <summary><see cref="BaseTypeCategory.Named"/> のときのパッケージ名。同一パッケージ相対参照なら null。</summary>
    public string? Package { get; }

    /// <summary><see cref="BaseTypeCategory.Named"/> のときの ROS 型名 (例 "Header")。</summary>
    public string? Name { get; }

    /// <summary>string の上限長 (<c>string&lt;=N</c>)。無指定なら null。</summary>
    public int? StringBound { get; }

    public ArrayKind ArrayKind { get; }

    /// <summary>FixedSize なら要素数、Bounded なら上限。それ以外は 0。</summary>
    public int ArrayLength { get; }

    public FieldType(
        BaseTypeCategory category,
        string? primitiveName,
        string? package,
        string? name,
        int? stringBound,
        ArrayKind arrayKind,
        int arrayLength)
    {
        Category = category;
        PrimitiveName = primitiveName;
        Package = package;
        Name = name;
        StringBound = stringBound;
        ArrayKind = arrayKind;
        ArrayLength = arrayLength;
    }

    public bool IsArray => ArrayKind != ArrayKind.None;
}
