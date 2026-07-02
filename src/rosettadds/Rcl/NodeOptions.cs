using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Rcl;

/// <summary>
/// <see cref="Node"/> の構成オプション。初回リリースでは Logger override のみ。
/// 将来的に namespace / parameter_override などを追加する。
/// </summary>
public sealed class NodeOptions
{
    /// <summary>Node 専用ロガー。null のとき Context の Logger を使う。</summary>
    public ILogger? Logger { get; init; }

    /// <summary>既定の <see cref="NodeOptions"/> インスタンス。</summary>
    public static NodeOptions Default { get; } = new();
}
