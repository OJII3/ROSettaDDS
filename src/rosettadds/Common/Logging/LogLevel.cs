namespace ROSettaDDS.Common.Logging;

/// <summary>ログレベル。低い数値ほど詳細。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    None = int.MaxValue,
}
