namespace RaindropToFloccus.Models;

/// <summary>
/// Each current value is compared with its own endpoint baseline, using the entire entity.
/// Null represents absence. A conflict means both sides changed to different values;
/// identical edits and deletion on both sides are not conflicts.
/// </summary>
public sealed record EntityComparison<TId, TEntity>(
    TId Id,
    TEntity? PreviousXbel,
    TEntity? PreviousRaindrop,
    TEntity? CurrentXbel,
    TEntity? CurrentRaindrop,
    SynchronizationChangeKind XbelChange,
    SynchronizationChangeKind RaindropChange,
    bool IsConflict)
    where TId : notnull
    where TEntity : class;
