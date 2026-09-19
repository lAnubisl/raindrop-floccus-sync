namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationService
{
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}
