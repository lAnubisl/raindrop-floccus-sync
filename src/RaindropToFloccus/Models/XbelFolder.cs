namespace RaindropToFloccus.Models;

public sealed record XbelFolder(
    long Id,
    string Title,
    IReadOnlyList<XbelItem> Children) : XbelItem(Id, Title);
