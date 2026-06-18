using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class MatchWaiterTests
{
    [Fact]
    public void minCount_0_は即_true_を返す()
    {
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => 0, minCount: 0, timeout: TimeSpan.FromSeconds(1))
            .GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void 即時達成済みなら_true()
    {
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => 5, minCount: 3, timeout: TimeSpan.FromMilliseconds(100))
            .GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void タイムアウトで_false()
    {
        int counter = 0;
        bool result = MatchWaiter.WaitUntilMatchedAsync(
            () => counter, minCount: 1, timeout: TimeSpan.FromMilliseconds(50))
            .GetAwaiter().GetResult();
        Assert.False(result);
    }

    [Fact]
    public void ポーリング_中に_達成したら_true()
    {
        int counter = 0;
        var task = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 3,
            timeout: TimeSpan.FromSeconds(2));
        // 50ms 後にカウンタを上げる
        Task.Run(async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref counter);
            await Task.Delay(20);
            Interlocked.Increment(ref counter);
            await Task.Delay(20);
            Interlocked.Increment(ref counter);
        });
        bool result = task.GetAwaiter().GetResult();
        Assert.True(result);
    }

    [Fact]
    public void 事前キャンセルで_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            MatchWaiter.WaitUntilMatchedAsync(
                () => 0, minCount: 1, timeout: TimeSpan.FromSeconds(1),
                cancellationToken: cts.Token)
                .GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void 待機中のキャンセルで_OperationCanceledException()
    {
        int counter = 0;
        using var cts = new CancellationTokenSource();
        var task = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 1,
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);
        // 50ms 後にキャンセル
        Task.Run(async () =>
        {
            await Task.Delay(50);
            cts.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
    }
}
