namespace RaindropToFloccus.Models;

public sealed record PendingFolderMapping(StableFolderId StableId, long XbelId, long? RaindropId);
