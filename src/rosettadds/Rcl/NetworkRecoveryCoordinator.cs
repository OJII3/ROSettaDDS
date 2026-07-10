using ROSettaDDS.Common.Logging;
using ROSettaDDS.Transport;

namespace ROSettaDDS.Rcl;

internal sealed class NetworkRecoveryCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int DefaultMaxAttempts = 3;

    private readonly INetworkChangeSource _source;
    private readonly Func<CancellationToken, ValueTask> _recover;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _workers = new();

    private CancellationTokenSource? _pendingCts;
    private bool _disposed;

    public NetworkRecoveryCoordinator(
        INetworkChangeSource source,
        Func<CancellationToken, ValueTask> recover,
        ILogger logger,
        TimeSpan? debounceDelay = null,
        TimeSpan? retryDelay = null,
        int? maxAttempts = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _recover = recover ?? throw new ArgumentNullException(nameof(recover));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxAttempts = maxAttempts ?? DefaultMaxAttempts;

        if (_debounceDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        if (_retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (_maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        _source.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public void Dispose()
    {
        Task[] workers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _source.NetworkAddressChanged -= OnNetworkAddressChanged;
            _lifetimeCts.Cancel();
            _pendingCts?.Cancel();
            workers = _workers.ToArray();
        }

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
                   static inner => inner is OperationCanceledException))
        {
        }

        lock (_sync)
        {
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
        _lifetimeCts.Dispose();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _pendingCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _pendingCts = cts;

            var worker = Task.Run(() => DebounceAndRecoverAsync(cts.Token));
            _workers.Add(worker);
            _ = worker.ContinueWith(
                completed => OnWorkerCompleted(completed, cts),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task DebounceAndRecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    await _recover(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        $"Network recovery attempt {attempt}/{_maxAttempts} failed",
                        ex);
                    if (attempt < _maxAttempts)
                    {
                        await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnWorkerCompleted(Task worker, CancellationTokenSource cts)
    {
        lock (_sync)
        {
            _workers.Remove(worker);
            if (ReferenceEquals(_pendingCts, cts))
            {
                _pendingCts = null;
            }
        }
        cts.Dispose();
    }
}
