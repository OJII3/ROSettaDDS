namespace ROSettaDDS.Common.Logging;

/// <summary>
/// rosettadds 内部用の最小ロガー抽象。
/// Unity では UnityEngine.Debug.Log にブリッジする実装を別パッケージで提供する。
/// </summary>
public interface ILogger
{
    bool IsEnabled(LogLevel level);
    void Log(LogLevel level, string message, Exception? exception = null);
}

/// <summary>ILogger の便利拡張メソッド。</summary>
public static class LoggerExtensions
{
    public static void Trace(this ILogger logger, string message) => logger.Log(LogLevel.Trace, message);
    public static void Debug(this ILogger logger, string message) => logger.Log(LogLevel.Debug, message);
    public static void Info(this ILogger logger, string message) => logger.Log(LogLevel.Info, message);
    public static void Warn(this ILogger logger, string message, Exception? ex = null) => logger.Log(LogLevel.Warn, message, ex);
    public static void Error(this ILogger logger, string message, Exception? ex = null) => logger.Log(LogLevel.Error, message, ex);
}
