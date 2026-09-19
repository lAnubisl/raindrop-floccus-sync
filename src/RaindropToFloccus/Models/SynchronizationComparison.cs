namespace RaindropToFloccus.Models;

/// <summary>
/// Includes unchanged entities as well as changes. Lists are ordered by stable ID,
/// not by source order. This is a comparison, not an executable reconciliation plan.
/// </summary>
public sealed record SynchronizationComparison(
    long BaseGeneration,
    SourceBookmarkTree Xbel,
    SourceBookmarkTree Raindrop,
    IReadOnlyList<EntityComparison<StableFolderId, BookmarkTreeFolder>> Folders,
    IReadOnlyList<EntityComparison<StableBookmarkId, BookmarkTreeBookmark>> Bookmarks);
