using ROSettaDDS.Common.Logging;

namespace ROSettaDDS.Dds;

internal sealed class SedpEndpointAdvertiser
{
    private static readonly TimeSpan UnregisterTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;
    private readonly Func<CancellationToken> _cancellationTokenProvider;
    private readonly Func<bool> _isDisposed;

    public SedpEndpointAdvertiser(
        ILogger logger,
        Func<CancellationToken> cancellationTokenProvider,
        Func<bool>? isDisposed = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cancellationTokenProvider = cancellationTokenProvider
            ?? throw new ArgumentNullException(nameof(cancellationTokenProvider));
        _isDisposed = isDisposed ?? (() => false);
    }

    public async Task RunAsync(Func<CancellationToken, ValueTask> operation, string failureMessage)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (failureMessage is null) throw new ArgumentNullException(nameof(failureMessage));

        var token = _cancellationTokenProvider();
        try
        {
            await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_isDisposed() || token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn(failureMessage, ex);
        }
    }

    public void WaitForUnregister(ValueTask unregisterTask)
    {
        try
        {
            var task = unregisterTask.AsTask();
            if (!task.Wait(UnregisterTimeout))
            {
                _logger.Warn("DomainParticipant timed out while sending SEDP unregister");
            }
        }
        catch (AggregateException ex)
        {
            _logger.Warn("DomainParticipant failed to send SEDP unregister", ex);
        }
        catch (Exception ex)
        {
            _logger.Warn("DomainParticipant failed to send SEDP unregister", ex);
        }
    }
}
