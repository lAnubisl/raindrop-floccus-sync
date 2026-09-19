namespace RaindropToFloccus.Models;

public sealed record XbelDocument(
    long HighestId,
    IReadOnlyList<XbelItem> Items);
