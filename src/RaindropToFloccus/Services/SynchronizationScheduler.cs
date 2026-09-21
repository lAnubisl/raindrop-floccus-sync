namespace RaindropToFloccus.Services;

public sealed class SynchronizationScheduler : ISynchronizationScheduler
{
    private readonly ISynchronizationService _synchronizationService;
    private readonly ApplicationConfigurationProvider _configurationProvider;
    private readonly IHealthStatusProvider _healthStatusProvider;
    private readonly ApplicationLogger _logger;

    public SynchronizationScheduler(
        ISynchronizationService synchronizationService,
        ApplicationConfigurationProvider configurationProvider,
        IHealthStatusProvider healthStatusProvider,
        ApplicationLogger logger)
    {
        _synchronizationService = synchronizationService;
        _configurationProvider = configurationProvider;
        _healthStatusProvider = healthStatusProvider;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Info("Synchronization worker started.");

        try
        {
            while (true)
            {
                await RunCycleAsync(cancellationToken);
                _logger.Info($"Next synchronization is scheduled in {_configurationProvider.SynchronizationInterval}.");
                await Task.Delay(_configurationProvider.SynchronizationInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Synchronization worker stopped.");
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        _logger.Info("Synchronization cycle started.");
        for (var retryAttempt = 0; ; retryAttempt++)
        {
            try
            {
                await _synchronizationService.SynchronizeAsync(cancellationToken);
                _healthStatusProvider.MarkHealthy();
                _logger.Info(retryAttempt == 0
                    ? "Synchronization cycle completed."
                    : $"Synchronization cycle completed after {retryAttempt} retry attempt(s).");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = ClassifyFailure(exception);
                if (failure == SynchronizationFailureKind.Fatal)
                {
                    _healthStatusProvider.MarkDegraded();
                    _logger.Error($"Synchronization worker is stopping because of a fatal error: {DescribeFailure(exception)}");
                    throw;
                }

                if (failure != SynchronizationFailureKind.Retryable)
                {
                    _healthStatusProvider.MarkDegraded();
                    LogDeferredFailure(failure, exception);
                    return;
                }

                if (retryAttempt >= _configurationProvider.MaximumRetryAttempts)
                {
                    _healthStatusProvider.MarkDegraded();
                    _logger.Error($"Synchronization failed after {retryAttempt + 1} attempt(s): {DescribeFailure(exception)} "
                        + "The next regular cycle will retry it.");
                    return;
                }

                var delay = GetRetryDelay(exception);
                _logger.Warning($"Synchronization attempt {retryAttempt + 1} failed with a transient error: {DescribeFailure(exception)} "
                    + $"Retrying in {delay}.");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private void LogDeferredFailure(SynchronizationFailureKind failure, Exception exception)
    {
        var message = failure switch
        {
            SynchronizationFailureKind.RecoveryRequired =>
                $"Synchronization requires manual recovery: {DescribeFailure(exception)}",
            SynchronizationFailureKind.Deferred =>
                $"Synchronization cannot be retried immediately: {DescribeFailure(exception)} The next regular cycle will check again.",
            _ => throw new InvalidOperationException("Only deferred synchronization failures can be logged here.")
        };
        _logger.Error(message);
    }

    private SynchronizationFailureKind ClassifyFailure(Exception exception) => exception switch
    {
        SynchronizationRecoveryRequiredException => SynchronizationFailureKind.RecoveryRequired,
        XbelFormatException or SynchronizationStateFormatException or FileNotFoundException =>
            SynchronizationFailureKind.Fatal,
        RaindropApiException { IsAuthenticationFailure: true } => SynchronizationFailureKind.Fatal,
        RaindropApiException { IsTransient: true } => SynchronizationFailureKind.Retryable,
        RaindropApiException => SynchronizationFailureKind.Deferred,
        GitClientException => SynchronizationFailureKind.Retryable,
        _ => SynchronizationFailureKind.Retryable
    };

    private static string DescribeFailure(Exception exception) => exception switch
    {
        GitClientException gitFailure =>
            $"Git operation '{gitFailure.Operation}' failed with exit code {gitFailure.ExitCode}.",
        _ => exception.Message
    };

    private TimeSpan GetRetryDelay(Exception exception)
    {
        if (exception is RaindropApiException { RetryAfter: { } retryAfter })
        {
            return retryAfter > _configurationProvider.RetryInterval
                ? retryAfter
                : _configurationProvider.RetryInterval;
        }

        return _configurationProvider.RetryInterval;
    }
}
