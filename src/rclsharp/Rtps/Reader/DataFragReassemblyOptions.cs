namespace Rclsharp.Rtps.Reader;

/// <summary>
/// DATA_FRAG 再構成バッファの制限値。
/// 大容量 topic で未完成 sample が溜まり続けないようにする。
/// </summary>
public sealed class DataFragReassemblyOptions
{
    public const int DefaultMaxSampleSize = 16 * 1024 * 1024;
    public const int DefaultMaxBufferedSamples = 64;
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(5);

    public int MaxSampleSize { get; init; } = DefaultMaxSampleSize;
    public int MaxBufferedSamples { get; init; } = DefaultMaxBufferedSamples;
    public TimeSpan TimeToLive { get; init; } = DefaultTimeToLive;

    public static DataFragReassemblyOptions Default { get; } = new();

    public void Validate()
    {
        if (MaxSampleSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSampleSize), "MaxSampleSize must be greater than zero.");
        }
        if (MaxBufferedSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBufferedSamples), "MaxBufferedSamples must be greater than zero.");
        }
        if (TimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeToLive), "TimeToLive must be greater than zero.");
        }
    }
}
