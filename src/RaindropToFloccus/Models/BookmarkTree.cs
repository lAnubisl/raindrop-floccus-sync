namespace RaindropToFloccus.Models;

/// <summary>
/// A source snapshot normalized into an unordered logical tree.
/// Duplicate titles and URLs are preserved because identity is carried exclusively by stable IDs.
/// </summary>
public sealed record BookmarkTree(
    IReadOnlyList<BookmarkTreeFolder> Folders,
    IReadOnlyList<BookmarkTreeBookmark> Bookmarks);
