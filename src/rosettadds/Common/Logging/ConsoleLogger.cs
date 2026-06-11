namespace ROSettaDDS.Common.Logging;

/// <summary>System.Console に書き出す既定ロガー。テストとサンプルで使用。</summary>
public sealed class ConsoleLogger : ILogger
{
    private readonly LogLevel _minimumLevel;
    private readonly string _category;

    public ConsoleLogger(string category, LogLevel minimumLevel = LogLevel.Info)
    {
        _category = category;
        _minimumLevel = minimumLevel;
    }

    public bool IsEnabled(LogLevel level) => level >= _minimumLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level}] [{_category}] {message}";
        if (level >= LogLevel.Warn)
        {
            Console.Error.WriteLine(line);
            if (exception is not null)
            {
                Console.Error.WriteLine(exception);
            }
        }
        else
        {
            Console.Out.WriteLine(line);
            if (exception is not null)
            {
                Console.Out.WriteLine(exception);
            }
        }
    }
}

/// <summary>ログを破棄する no-op ロガー。デフォルトで使う。</summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    private NullLogger() { }
    public bool IsEnabled(LogLevel level) => false;
    public void Log(LogLevel level, string message, Exception? exception = null) { }
}
