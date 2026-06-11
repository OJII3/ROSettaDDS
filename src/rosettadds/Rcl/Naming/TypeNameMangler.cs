namespace ROSettaDDS.Rcl.Naming;

/// <summary>
/// ROS 2 メッセージ型名 ⇔ DDS 型名を変換する。
/// 例: "std_msgs/msg/String" → "std_msgs::msg::dds_::String_"
///
/// ROS 2 (rmw_fastrtps / rmw_cyclonedds) 共通の規約:
/// 1. パッケージとサブパス (msg/srv) を "::" で連結
/// 2. 末尾のメッセージ型の前に "dds_" 名前空間を挟む
/// 3. メッセージ型名の末尾にアンダースコア "_" を付ける
/// </summary>
public static class TypeNameMangler
{
    /// <summary>ROS 2 型名 → DDS 型名へ変換する。</summary>
    public static string MangleType(string rosType)
    {
        if (string.IsNullOrEmpty(rosType)) throw new ArgumentException("Value cannot be null or empty.", nameof(rosType));
        var parts = rosType.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            // フォーマット不明の場合は何もせずに返す
            return rosType;
        }
        var typeName = parts[^1];
        var nsPath = string.Join("::", parts[..^1]);
        return $"{nsPath}::dds_::{typeName}_";
    }

    /// <summary>DDS 型名 → ROS 2 型名へ変換する。"dds_" と末尾 "_" を除去。</summary>
    public static string DemangleType(string ddsType)
    {
        if (ddsType is null) throw new ArgumentNullException(nameof(ddsType));
        var parts = ddsType.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[^2] != "dds_")
        {
            return ddsType;
        }
        var typeName = parts[^1];
        if (typeName.EndsWith('_'))
        {
            typeName = typeName[..^1];
        }
        // パッケージ + サブパスを "/" で再結合
        var nsPath = string.Join("/", parts[..^2]);
        return string.IsNullOrEmpty(nsPath) ? typeName : $"{nsPath}/{typeName}";
    }
}
