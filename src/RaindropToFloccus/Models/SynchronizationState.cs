namespace RaindropToFloccus.Models;

/// <summary>
/// The last fully reconciled view of both synchronization endpoints.
/// A state file is written only after initialization has completed successfully.
/// </summary>
public sealed record SynchronizationState(
    int SchemaVersion,
    long Generation,
    bool InitializationCompleted,
    BookmarkTree XbelSnapshot,
    BookmarkTree RaindropSnapshot,
    IReadOnlyList<FolderIdentityMapping> FolderMappings,
    IReadOnlyList<BookmarkIdentityMapping> BookmarkMappings);
