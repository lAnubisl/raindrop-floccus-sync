namespace RaindropToFloccus.Models;

/// <summary>
/// Local write-ahead journal. Confirmed source IDs survive process restarts. An in-flight
/// write without a durably recorded response must never be replayed automatically.
/// </summary>
public sealed record SynchronizationJournal(
    int Version,
    string BaseRevision,
    string BaseStateContent,
    string SourceXbelContent,
    SynchronizationPlan Plan,
    IReadOnlyList<RaindropCollection> ExpectedCollections,
    IReadOnlyList<RaindropBookmark> ExpectedBookmarks,
    string? InFlightOperation,
    string? FinalStateContent,
    int AppliedOperations);
