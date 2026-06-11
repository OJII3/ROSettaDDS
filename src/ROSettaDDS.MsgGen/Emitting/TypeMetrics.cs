using System.Collections.Generic;
using ROSettaDDS.MsgGen.Model;
using ROSettaDDS.MsgGen.TypeMapping;

namespace ROSettaDDS.MsgGen.Emitting;

/// <summary>固定長型のサイズと先頭アライメント。</summary>
public readonly struct SizeInfo
{
    /// <summary>静的にサイズが確定する (string/sequence を含まない) か。</summary>
    public bool IsFixed { get; }

    /// <summary><see cref="IsFixed"/> のときのバイトサイズ。</summary>
    public int FixedSize { get; }

    /// <summary>この値を書き始める際に必要な CDR アライメント。</summary>
    public int LeadingAlignment { get; }

    public SizeInfo(bool isFixed, int fixedSize, int leadingAlignment)
    {
        IsFixed = isFixed;
        FixedSize = fixedSize;
        LeadingAlignment = leadingAlignment;
    }
}

/// <summary>
/// メッセージ型・フィールド型の「固定サイズか」「先頭アライメント」を再帰解決する。
/// CDR の整列パディングを正確に見積もるために使う。未登録のネスト型は
/// 安全側 (可変・最大アライメント 8) として扱う。
/// </summary>
public sealed class TypeMetrics
{
    /// <summary>CDR の最大プリミティブアライメント (float64/int64)。</summary>
    private const int MaxAlignment = 8;

    private readonly MessageRegistry _registry;
    private readonly Dictionary<string, SizeInfo> _cache = new();
    private readonly HashSet<string> _inProgress = new();

    public TypeMetrics(MessageRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>フィールド型 (配列含む) のサイズ情報。</summary>
    public SizeInfo OfField(FieldType t, string ownPackage)
    {
        if (t.IsArray)
        {
            SizeInfo elem = OfElement(t, ownPackage);
            int leading = t.ArrayKind == ArrayKind.FixedSize ? elem.LeadingAlignment : 4; // seq は length(uint32)
            if (t.ArrayKind == ArrayKind.FixedSize && elem.IsFixed && t.Category == BaseTypeCategory.Primitive)
            {
                // プリミティブはサイズ=アライメントで密に並ぶため固定。
                return new SizeInfo(true, t.ArrayLength * elem.FixedSize, leading);
            }
            // それ以外の配列 (可変長, 文字列/ネスト要素) は可変扱い。
            return new SizeInfo(false, 0, leading);
        }
        return OfElement(t, ownPackage);
    }

    /// <summary>要素 (非配列) としてのサイズ情報。</summary>
    private SizeInfo OfElement(FieldType t, string ownPackage)
    {
        switch (t.Category)
        {
            case BaseTypeCategory.Primitive:
                int sz = PrimitiveTypes.Get(t.PrimitiveName!).Size;
                return new SizeInfo(true, sz, sz);
            case BaseTypeCategory.String:
                return new SizeInfo(false, 0, 4);
            case BaseTypeCategory.Named:
                string pkg = t.Package ?? ownPackage;
                return OfMessage(pkg, t.Name!);
            default:
                return new SizeInfo(false, 0, MaxAlignment);
        }
    }

    /// <summary>メッセージ型のサイズ情報 (再帰・メモ化・循環保護)。</summary>
    public SizeInfo OfMessage(string package, string name)
    {
        string key = package + "/" + name;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (!_registry.TryGet(package, name, out var def) || !_inProgress.Add(key))
        {
            // 未登録、または循環参照。安全側で可変・最大アライメント。
            return new SizeInfo(false, 0, MaxAlignment);
        }

        SizeInfo result;
        if (def.Fields.Count == 0)
        {
            // rosidl の空メッセージは 1 バイトの structure dummy (固定)。
            result = new SizeInfo(true, 1, 1);
        }
        else
        {
            int offset = 0;
            bool isFixed = true;
            int leading = OfField(def.Fields[0].Type, package).LeadingAlignment;
            foreach (var f in def.Fields)
            {
                SizeInfo fi = OfField(f.Type, package);
                offset = Align(offset, fi.LeadingAlignment);
                if (fi.IsFixed)
                {
                    offset += fi.FixedSize;
                }
                else
                {
                    isFixed = false;
                    break;
                }
            }
            result = new SizeInfo(isFixed, isFixed ? offset : 0, leading);
        }

        _inProgress.Remove(key);
        _cache[key] = result;
        return result;
    }

    public static int Align(int offset, int alignment)
    {
        if (alignment <= 1) return offset;
        int rem = offset % alignment;
        return rem == 0 ? offset : offset + (alignment - rem);
    }
}
