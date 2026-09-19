using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationComparer
{
    /// <summary>
    /// Compares complete, freshly read endpoint snapshots against the last reconciled state.
    /// Performs no I/O, resolves no structural conflicts and does not advance the state.
    /// Unknown source IDs receive distinct provisional identities for this comparison only.
    /// </summary>
    SynchronizationComparison Compare(
        SynchronizationState state,
        XbelDocument xbel,
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> bookmarks);
}
