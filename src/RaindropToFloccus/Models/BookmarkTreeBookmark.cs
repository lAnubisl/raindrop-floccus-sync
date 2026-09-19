namespace RaindropToFloccus.Models;

/// <param name="Id">Stable identity shared by representations of this bookmark on both sides.</param>
/// <param name="ParentId">
/// Null when the bookmark is at the XBEL root and therefore belongs to Raindrop Unsorted.
/// </param>
/// <param name="Title">The title as represented in this particular source snapshot.</param>
/// <param name="Url">The URL as represented in this particular source snapshot.</param>
public sealed record BookmarkTreeBookmark(
    StableBookmarkId Id,
    StableFolderId? ParentId,
    string Title,
    string Url);
