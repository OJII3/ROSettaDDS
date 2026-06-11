namespace ROSettaDDS.Common;

/// <summary>
/// Hex 文字列変換のポータブル実装。
/// <see cref="Convert.ToHexString(ReadOnlySpan{byte})"/> は .NET 5+ の API であり
/// netstandard2.1 には存在しないため、同じ形式 (大文字、区切りなし) を自前で提供する。
/// </summary>
internal static class HexUtil
{
    private const string HexChars = "0123456789ABCDEF";

    public static string ToHexString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = HexChars[bytes[i] >> 4];
            chars[i * 2 + 1] = HexChars[bytes[i] & 0xF];
        }
        return new string(chars);
    }
}
