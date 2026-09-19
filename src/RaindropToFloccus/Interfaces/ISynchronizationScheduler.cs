namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationScheduler
{
    Task RunAsync(CancellationToken cancellationToken);
}

