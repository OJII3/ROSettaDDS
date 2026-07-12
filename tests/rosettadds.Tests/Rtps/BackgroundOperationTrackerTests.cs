using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rtps.Writer;

namespace ROSettaDDS.Tests.Rtps;

public class BackgroundOperationTrackerTests
{
    [Fact]
    public void 正常完了したタスクは待機不要()
    {
        var tracker = new BackgroundOperationTracker();
        var completed = false;
        tracker.Run(async ct =>
        {
            await Task.Delay(10, ct);
            completed = true;
        }, "test", CancellationToken.None);

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
        completed.Should().BeTrue();
    }

    [Fact]
    public void キャンセル例外は警告なしで正常終了()
    {
        var tracker = new BackgroundOperationTracker();
        tracker.Run(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
        }, "test", new CancellationToken(true));

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void 通常例外はWarnでログされる()
    {
        var logger = new CollectingLogger();
        var tracker = new BackgroundOperationTracker(logger);
        tracker.Run(_ => throw new InvalidOperationException("boom"), "test", CancellationToken.None);

        tracker.WaitForCompletion(TimeSpan.FromSeconds(1));
        logger.Warns.Should().Contain(m => m.Contains("test"));
    }

    [Fact]
    public void 複数タスクを並列実行して待機する()
    {
        var counter = 0;
        var tracker = new BackgroundOperationTracker();
        for (int i = 0; i < 5; i++)
        {
            tracker.Run(async ct =>
            {
                await Task.Delay(10, ct);
                Interlocked.Increment(ref counter);
            }, $"task{i}", CancellationToken.None);
        }

        tracker.WaitForCompletion(TimeSpan.FromSeconds(2));
        Volatile.Read(ref counter).Should().Be(5);
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warns { get; } = new();
        public bool IsEnabled(LogLevel level) => true;
        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            if (level == LogLevel.Warn) Warns.Add(message);
        }
    }
}
