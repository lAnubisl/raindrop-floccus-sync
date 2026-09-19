namespace RaindropToFloccus.Interfaces;

public interface IInitialSynchronizationService
{
    /// <summary>
    /// Initializes Raindrop from the Git XBEL file when no synchronization state exists.
    /// Returns true when initialization was performed and false when a valid state already exists.
    /// </summary>
    Task<bool> InitializeIfRequiredAsync(CancellationToken cancellationToken = default);
}
