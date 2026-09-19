namespace RaindropToFloccus.Models;

internal sealed record InitialSynchronizationSourceBookmark(
    XbelBookmark Bookmark,
    long? ParentXbelId,
    StableBookmarkId StableId);
