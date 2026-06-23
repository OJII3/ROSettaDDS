using System.Threading;
using System.Threading.Tasks;
using ROSettaDDS.Dds;

namespace ROSettaDDS.Tests.Dds;

public class MatchWaiterTests
{
    [Fact]
    public async Task minCount_0_は即_true_を返す()
    {
        bool result = await MatchWaiter.WaitUntilMatchedAsync(
            () => 0, minCount: 0, timeout: TimeSpan.FromSeconds(1));
        Assert.True(result);
    }

    [Fact]
    public async Task 即時達成済みなら_true()
    {
        bool result = await MatchWaiter.WaitUntilMatchedAsync(
            () => 5, minCount: 3, timeout: TimeSpan.FromMilliseconds(100));
        Assert.True(result);
    }

    [Fact]
    public async Task タイムアウトで_false()
    {
        int counter = 0;
        bool result = await MatchWaiter.WaitUntilMatchedAsync(
            () => counter, minCount: 1, timeout: TimeSpan.FromMilliseconds(50));
        Assert.False(result);
    }

    [Fact]
    public async Task ポーリング_中に_達成したら_true()
    {
        int counter = 0;
        var waitTask = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 3,
            timeout: TimeSpan.FromSeconds(2));
        var pumpThread = new Thread(() =>
        {
            Thread.Sleep(50);
            Interlocked.Increment(ref counter);
            Thread.Sleep(20);
            Interlocked.Increment(ref counter);
            Thread.Sleep(20);
            Interlocked.Increment(ref counter);
        })
        { IsBackground = true, Name = "MatchWaiterTest-Pump" };
        pumpThread.Start();
        bool result = await waitTask;
        pumpThread.Join();
        Assert.True(result);
    }

    [Fact]
    public async Task 事前キャンセルで_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MatchWaiter.WaitUntilMatchedAsync(
                () => 0, minCount: 1, timeout: TimeSpan.FromSeconds(1),
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task 待機中のキャンセルで_OperationCanceledException()
    {
        int counter = 0;
        using var cts = new CancellationTokenSource();
        var waitTask = MatchWaiter.WaitUntilMatchedAsync(
            () => Volatile.Read(ref counter),
            minCount: 1,
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);
        var cancelTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            cts.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask);
        await cancelTask;
    }
}
