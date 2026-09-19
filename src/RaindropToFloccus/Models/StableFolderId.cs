namespace RaindropToFloccus.Models;

/// <summary>
/// Identifies one logical folder across XBEL and Raindrop snapshots.
/// The value is independent of the source-specific XBEL and Raindrop identifiers.
/// </summary>
public readonly record struct StableFolderId(Guid Value);
