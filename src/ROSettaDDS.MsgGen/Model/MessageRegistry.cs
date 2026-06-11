using System.Collections.Generic;

namespace ROSettaDDS.MsgGen.Model;

/// <summary>
/// パッケージ/型名から <see cref="MessageDefinition"/> を引く登録簿。
/// エミッタがネスト型のサイズ・アライメントを再帰的に解決するために使う。
/// 未登録の型 (外部パッケージなど) は「サイズ可変・最大アライメント」として安全側に扱う。
/// </summary>
public sealed class MessageRegistry
{
    private readonly Dictionary<string, MessageDefinition> _defs = new();

    public MessageRegistry()
    {
    }

    public MessageRegistry(IEnumerable<MessageDefinition> defs)
    {
        foreach (var d in defs)
        {
            Add(d);
        }
    }

    public void Add(MessageDefinition def) => _defs[Key(def.Package, def.Name)] = def;

    public bool TryGet(string package, string name, out MessageDefinition def) =>
        _defs.TryGetValue(Key(package, name), out def!);

    private static string Key(string package, string name) => package + "/" + name;
}
