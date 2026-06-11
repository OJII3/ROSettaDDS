namespace ROSettaDDS.Cdr;

/// <summary>
/// CDR シリアライズ規約。各メッセージ型は対応する <see cref="ICdrSerializer{T}"/> を提供する。
/// IL2CPP / AOT 互換のため、メッセージ型ごとに手書き実装を用意する方針。
/// 通常は struct で実装し、ジェネリック制約 <c>where TSer : struct, ICdrSerializer&lt;T&gt;</c> 経由で呼ぶ。
/// </summary>
public interface ICdrSerializer<T>
{
    /// <summary>シリアライズ後のサイズを概算 (可変長は最大値)。バッファ確保用。</summary>
    int GetSerializedSize(in T value);

    /// <summary>value をライタへ書き込む。エンキャプスレーションヘッダは含まない。</summary>
    void Serialize(ref CdrWriter writer, in T value);

    /// <summary>リーダから value を読み出す。エンキャプスレーションヘッダは事前に剥がしておく。</summary>
    void Deserialize(ref CdrReader reader, out T value);

    /// <summary>キー付きトピック (Keyed Topic) かどうか。</summary>
    bool IsKeyed { get; }

    /// <summary>キーフィールドのみをシリアライズ。<see cref="IsKeyed"/>=false の場合は no-op。</summary>
    void SerializeKey(ref CdrWriter writer, in T value);
}
