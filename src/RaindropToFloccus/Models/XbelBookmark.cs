namespace RaindropToFloccus.Models;

public sealed record XbelBookmark(
    long Id,
    string Title,
    string Url) : XbelItem(Id, Title);
