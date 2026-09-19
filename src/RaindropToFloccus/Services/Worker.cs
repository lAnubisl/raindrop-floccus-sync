using RaindropToFloccus.Interfaces;

namespace RaindropToFloccus.Services;

public sealed class Worker : BackgroundService
{
    private readonly ISynchronizationScheduler _synchronizationScheduler;

    public Worker(ISynchronizationScheduler synchronizationScheduler)
    {
        _synchronizationScheduler = synchronizationScheduler;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _synchronizationScheduler.RunAsync(stoppingToken);
    }
}

