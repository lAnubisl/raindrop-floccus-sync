namespace RaindropToFloccus.Models;

/// <summary>
/// Current logical tree and the source IDs of its surviving and newly discovered entities.
/// New stable IDs are provisional; these are not confirmed two-sided identity mappings.
/// </summary>
public sealed record SourceBookmarkTree(
    BookmarkTree Tree,
    IReadOnlyDictionary<StableFolderId, long> FolderSourceIds,
    IReadOnlyDictionary<StableBookmarkId, long> BookmarkSourceIds);
