namespace RaindropToFloccus.Models;

/// <summary>Desired endpoint values may differ for unchanged server-normalized fields.</summary>
public sealed record SynchronizationPlan(
    BookmarkTree XbelTree,
    BookmarkTree RaindropTree,
    IReadOnlyList<PendingFolderMapping> Folders,
    IReadOnlyList<PendingBookmarkMapping> Bookmarks,
    string XbelContent);
