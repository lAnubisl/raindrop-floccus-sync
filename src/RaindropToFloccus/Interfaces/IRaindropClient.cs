using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface IRaindropClient
{
    /// <summary>Combines identical collection IDs across endpoints, rejecting conflicting synchronized fields.</summary>
    Task<IReadOnlyList<RaindropCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RaindropBookmark>> GetActiveBookmarksAsync(CancellationToken cancellationToken = default);

    Task<RaindropCollection> CreateCollectionAsync(
        RaindropCollectionWrite collection, CancellationToken cancellationToken = default);

    Task<RaindropCollection> UpdateCollectionAsync(
        long id, RaindropCollectionWrite collection, CancellationToken cancellationToken = default);

    /// <summary>Removes only the explicitly listed user collections. Include descendants explicitly.</summary>
    Task DeleteCollectionsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates batches of at most 100 items, yielding each confirmed batch before starting the next.
    /// Persist confirmed IDs before advancing. On failure, earlier batches may already be committed;
    /// the failing batch may also have been applied. Do not blindly replay creation requests.
    /// Returned order is the API response order, not a guaranteed input-to-ID mapping.
    /// Returned fields reflect server normalization; titles are not guaranteed to equal the input.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<RaindropBookmark>> CreateBookmarksAsync(
        IReadOnlyList<RaindropBookmarkWrite> bookmarks, CancellationToken cancellationToken = default);

    /// <summary>Sends the supplied fields and returns the stored server representation, including normalized titles.</summary>
    Task<RaindropBookmark> UpdateBookmarkAsync(
        long id, RaindropBookmarkWrite bookmark, CancellationToken cancellationToken = default);

    /// <summary>Moves explicit IDs from one active collection. Earlier batches may succeed before a failure.</summary>
    Task MoveBookmarksAsync(long sourceCollectionId, long targetCollectionId,
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default);

    /// <summary>Moves explicit IDs to Trash, never permanently deletes. Earlier batches may succeed before a failure.</summary>
    Task TrashBookmarksAsync(long sourceCollectionId,
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default);
}
