namespace RaindropToFloccus.Models;

public sealed record PendingBookmarkMapping(StableBookmarkId StableId, long XbelId, long? RaindropId);
