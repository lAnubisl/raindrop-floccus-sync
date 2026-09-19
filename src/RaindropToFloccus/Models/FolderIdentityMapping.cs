namespace RaindropToFloccus.Models;

/// <summary>Maps one logical folder to its source-specific identifiers.</summary>
public sealed record FolderIdentityMapping(
    StableFolderId StableId,
    long XbelId,
    long RaindropCollectionId);
