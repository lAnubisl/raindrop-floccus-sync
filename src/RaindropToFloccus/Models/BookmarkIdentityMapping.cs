namespace RaindropToFloccus.Models;

/// <summary>Maps one logical bookmark to its source-specific identifiers.</summary>
public sealed record BookmarkIdentityMapping(
    StableBookmarkId StableId,
    long XbelId,
    long RaindropBookmarkId);
