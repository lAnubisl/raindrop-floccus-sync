namespace RaindropToFloccus.Models;

/// <summary>
/// Identifies one logical bookmark across XBEL and Raindrop snapshots.
/// The value is independent of its URL and source-specific identifiers.
/// </summary>
public readonly record struct StableBookmarkId(Guid Value);
