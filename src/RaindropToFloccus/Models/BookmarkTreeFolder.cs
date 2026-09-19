namespace RaindropToFloccus.Models;

/// <param name="Id">Stable identity shared by representations of this folder on both sides.</param>
/// <param name="ParentId">Null when the folder is directly under the tree root.</param>
/// <param name="Title">The title as represented in this particular source snapshot.</param>
public sealed record BookmarkTreeFolder(
    StableFolderId Id,
    StableFolderId? ParentId,
    string Title);
