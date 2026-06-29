using ROSettaDDS.Common.Logging;
using ROSettaDDS.Discovery;

namespace ROSettaDDS.Dds;

internal sealed class LeaseExpiryMonitor : IDisposable
{
    private static readonly TimeSpan MaxCheckPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinCheckPeriod = TimeSpan.FromMilliseconds(50);

    private readonly DiscoveryDb _discoveryDb;
    private readonly ILogger _logger;
    private readonly TimeSpan _checkPeriod;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public LeaseExpiryMonitor(DiscoveryDb discoveryDb, DomainParticipantOptions options, ILogger logger)
    {
        _discoveryDb = discoveryDb ?? throw new ArgumentNullException(nameof(discoveryDb));
        if (options is null) throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _checkPeriod = ComputeCheckPeriod(options);
    }

    public CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;

    public static TimeSpan ComputeCheckPeriod(DomainParticipantOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var period = MinPositive(MaxCheckPeriod, options.SpdpInterval);
        var leaseDuration = options.LeaseDuration.ToTimeSpan();
        if (leaseDuration > TimeSpan.Zero)
        {
            var leaseQuarter = TimeSpan.FromTicks(Math.Max(1L, leaseDuration.Ticks / 4L));
            period = MinPositive(period, leaseQuarter);
        }
        return period < MinCheckPeriod ? MinCheckPeriod : period;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }
        catch (Exception ex)
        {
            _logger.Warn("DomainParticipant lease expiry loop did not exit cleanly", ex);
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkPeriod, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _discoveryDb.ExpireOldParticipants(DateTime.UtcNow);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    private static TimeSpan MinPositive(TimeSpan left, TimeSpan right)
    {
        if (left <= TimeSpan.Zero)
        {
            return right;
        }
        if (right <= TimeSpan.Zero)
        {
            return left;
        }
        return left <= right ? left : right;
    }
}
