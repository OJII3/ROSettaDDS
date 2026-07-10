using System.Net.NetworkInformation;
using System.Net.Sockets;
using ROSettaDDS.Common.Logging;
using ROSettaDDS.Rcl;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Tests.Rcl;

public class NetworkRecoveryCoordinatorTests
{
    [Fact]
    public async Task 連続通知は最後の通知からデバウンスして1回復旧する()
    {
        var source = new FakeNetworkChangeSource();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new NetworkRecoveryCoordinator(
            source,
            _ =>
            {
                Interlocked.Increment(ref calls);
                recovered.TrySetResult();
                return default;
            },
            NullLogger.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(30),
            retryDelay: TimeSpan.FromMilliseconds(1));

        source.Raise();
        source.Raise();
        source.Raise();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task 一時失敗は最大3回まで再試行する()
    {
        var source = new FakeNetworkChangeSource();
        var attemptedThreeTimes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new NetworkRecoveryCoordinator(
            source,
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 3)
                {
                    attemptedThreeTimes.TrySetResult();
                }
                throw new SocketException((int)SocketError.NetworkDown);
            },
            NullLogger.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(1),
            retryDelay: TimeSpan.FromMilliseconds(1),
            maxAttempts: 3);

        source.Raise();
        await attemptedThreeTimes.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(20);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task 成功した時点で再試行を終了する()
    {
        var source = new FakeNetworkChangeSource();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new NetworkRecoveryCoordinator(
            source,
            _ =>
            {
                if (Interlocked.Increment(ref calls) < 2)
                {
                    throw new SocketException((int)SocketError.NetworkDown);
                }
                recovered.TrySetResult();
                return default;
            },
            NullLogger.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(1),
            retryDelay: TimeSpan.FromMilliseconds(1),
            maxAttempts: 3);

        source.Raise();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(20);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Disposeは購読を解除し以後の通知を無視する()
    {
        var source = new FakeNetworkChangeSource();
        var calls = 0;
        var coordinator = new NetworkRecoveryCoordinator(
            source,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return default;
            },
            NullLogger.Instance,
            debounceDelay: TimeSpan.FromMilliseconds(1),
            retryDelay: TimeSpan.FromMilliseconds(1));

        source.SubscriberCount.Should().Be(1);
        coordinator.Dispose();
        source.SubscriberCount.Should().Be(0);

        source.Raise();
        await Task.Delay(30);

        calls.Should().Be(0);
    }

    internal sealed class FakeNetworkChangeSource : INetworkChangeSource
    {
        private NetworkAddressChangedEventHandler? _networkAddressChanged;

        public int SubscriberCount { get; private set; }

        public event NetworkAddressChangedEventHandler? NetworkAddressChanged
        {
            add
            {
                _networkAddressChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _networkAddressChanged -= value;
                SubscriberCount--;
            }
        }

        public void Raise() => _networkAddressChanged?.Invoke(this, EventArgs.Empty);
    }
}
