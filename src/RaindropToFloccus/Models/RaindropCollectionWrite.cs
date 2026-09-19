namespace RaindropToFloccus.Models;

/// <param name="ParentId">Null for a root collection; otherwise a positive user collection ID.</param>
public sealed record RaindropCollectionWrite(string Title, long? ParentId = null);
