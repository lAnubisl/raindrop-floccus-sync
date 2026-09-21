namespace RaindropToFloccus.Interfaces;

public interface IGitRepositoryClient
{
    string WorkingDirectory { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    Task<string> GetCurrentRevisionAsync(CancellationToken cancellationToken = default);

    Task<bool> CommitSynchronizationFilesAsync(
        string message,
        CancellationToken cancellationToken = default);

    Task PushAsync(CancellationToken cancellationToken = default);

    Task<bool> PushSynchronizationFilesIfRemoteUnchangedAsync(
        string expectedRemoteRevision,
        XbelDocument previousXbel,
        XbelDocument currentXbel,
        CancellationToken cancellationToken = default);

    Task RestoreRemoteAfterRejectedSynchronizationPushAsync(
        string expectedRemoteRevision,
        CancellationToken cancellationToken = default);

    Task<bool> IsSynchronizationCommitAtHeadAsync(
        string expectedParentRevision,
        CancellationToken cancellationToken = default);
}
