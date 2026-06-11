namespace ROSettaDDS.Cdr;

/// <summary>
/// 外部 CDR payload を読むときの確保上限。
/// </summary>
public sealed class CdrReadLimits
{
    public const int DefaultMaxUserPayloadBytes = 16 * 1024 * 1024;

    public static readonly CdrReadLimits Default = new();

    public CdrReadLimits(
        int maxStringBytes = DefaultMaxUserPayloadBytes,
        int maxSequenceElements = DefaultMaxUserPayloadBytes,
        int maxSequenceBytes = DefaultMaxUserPayloadBytes)
    {
        if (maxStringBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStringBytes));
        }
        if (maxSequenceElements <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSequenceElements));
        }
        if (maxSequenceBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSequenceBytes));
        }

        MaxStringBytes = maxStringBytes;
        MaxSequenceElements = maxSequenceElements;
        MaxSequenceBytes = maxSequenceBytes;
    }

    /// <summary>CDR string の length フィールド値上限。NUL 終端を含む。</summary>
    public int MaxStringBytes { get; }

    /// <summary>sequence 要素数の上限。</summary>
    public int MaxSequenceElements { get; }

    /// <summary>固定長 sequence payload のバイト数上限。alignment padding は含めない。</summary>
    public int MaxSequenceBytes { get; }
}
