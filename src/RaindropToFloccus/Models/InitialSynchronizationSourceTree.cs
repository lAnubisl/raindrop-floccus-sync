namespace RaindropToFloccus.Models;

internal sealed record InitialSynchronizationSourceTree(
    IReadOnlyList<InitialSynchronizationSourceFolder> Folders,
    IReadOnlyList<InitialSynchronizationSourceBookmark> Bookmarks,
    IReadOnlyDictionary<long, InitialSynchronizationSourceFolder> FolderByXbelId,
    IReadOnlyDictionary<long, InitialSynchronizationSourceBookmark> BookmarkByXbelId,
    IReadOnlyDictionary<long, StableFolderId> StableFolderIdByXbelId,
    BookmarkTree XbelTree);
