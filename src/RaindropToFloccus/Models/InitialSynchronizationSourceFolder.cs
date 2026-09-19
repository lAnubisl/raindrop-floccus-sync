namespace RaindropToFloccus.Models;

internal sealed record InitialSynchronizationSourceFolder(
    XbelFolder Folder,
    long? ParentXbelId,
    StableFolderId StableId);
