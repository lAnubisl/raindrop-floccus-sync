namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationFileStore
{
    Task<string> ReadXbelAsync(CancellationToken cancellationToken = default);

    Task<string?> ReadStateIfExistsAsync(CancellationToken cancellationToken = default);

    Task WriteNewStateAtomicallyAsync(
        string content,
        CancellationToken cancellationToken = default);

    Task WriteXbelAtomicallyAsync(string content, CancellationToken cancellationToken = default);

    Task WriteStateAtomicallyAsync(string content, CancellationToken cancellationToken = default);
}
