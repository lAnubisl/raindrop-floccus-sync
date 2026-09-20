using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Helpers;
using RaindropToFloccus.Models;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Services;

public sealed class BidirectionalSynchronizationService : ISynchronizationService
{
    private readonly IGitRepositoryClient _git;
    private readonly ISynchronizationFileStore _files;
    private readonly ISynchronizationJournalStore _journals;
    private readonly IInitialSynchronizationService _initialization;
    private readonly ISynchronizationComparer _comparer;
    private readonly ISynchronizationPlanner _planner;
    private readonly ISynchronizationStateSerializer _states;
    private readonly IXbelDocumentSerializer _xbel;
    private readonly IRaindropClient _raindrop;
    private readonly ApplicationLogger _logger;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    public BidirectionalSynchronizationService(
        IGitRepositoryClient git, ISynchronizationFileStore files,
        ISynchronizationJournalStore journals, IInitialSynchronizationService initialization,
        ISynchronizationComparer comparer, ISynchronizationPlanner planner,
        ISynchronizationStateSerializer states, IXbelDocumentSerializer xbel,
        IRaindropClient raindrop, ApplicationLogger logger)
    {
        _git = git;
        _files = files;
        _journals = journals;
        _initialization = initialization;
        _comparer = comparer;
        _planner = planner;
        _states = states;
        _xbel = xbel;
        _raindrop = raindrop;
        _logger = logger;
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await _cycleLock.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeCoreAsync(cancellationToken);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private async Task SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        await _git.InitializeAsync(cancellationToken);
        while (true)
        {
            // A normal refresh is safe before any Raindrop write. A non-fast-forward here
            // can only be recovered automatically for the final local synchronization commit.
            var refreshed = await _git.RefreshAsync(cancellationToken);
            var journal = await _journals.ReadAsync(cancellationToken);
            if (!refreshed)
            {
                if (journal?.FinalStateContent is null)
                {
                    throw Recovery("Git diverged without a completed synchronization commit that can be safely reconciled.");
                }

                ValidateJournal(journal);
                // An interruption after either atomic file replacement leaves uncommitted
                // synchronization files. Commit only the allowed pair so the rejected commit
                // can be preserved before the working tree is restored to origin/main.
                await _git.CommitSynchronizationFilesAsync(
                    "Preserve synchronization files before Git reconciliation", cancellationToken);
                await _git.RestoreRemoteAfterRejectedSynchronizationPushAsync(journal.BaseRevision, cancellationToken);
                _logger.Warning("A competing Git commit was found after local finalization; recalculating from origin/main.");
                journal = await ReconcileJournalAsync(cancellationToken);
                if (journal is null) return;
            }
            else if (journal is null)
            {
                // Recovery precedes initialization: never let the initializer commit half of a file pair.
                if (await _initialization.InitializeIfRequiredAsync(cancellationToken)) return;
                journal = await CreateJournalIfChangedAsync(cancellationToken);
                if (journal is null) return;
            }
            else
            {
                ValidateJournal(journal);
                _logger.Info("Resuming a locally journaled synchronization.");
            }

            if (journal.InFlightOperation is not null)
                throw Recovery("A Raindrop write has no durably confirmed response. Automatic replay is disabled to prevent duplicate or destructive changes.");

            if (journal.FinalStateContent is null && !await SourceMatchesJournalAsync(journal, cancellationToken)
                || journal.FinalStateContent is not null && !await CanResumeFinalizationAsync(journal, cancellationToken))
            {
                _logger.Warning("Floccus changed Git while synchronization was pending; recalculating the plan from the latest revision.");
                journal = await ReconcileJournalAsync(cancellationToken);
                if (journal is null) return;
            }

            if (journal.FinalStateContent is null)
            {
                journal = await ApplyAndConfirmPlanAsync(journal, cancellationToken);
                if (journal is null)
                {
                    _logger.Warning("Floccus changed Git during Raindrop application; recalculating the plan from the latest revision.");
                    continue;
                }
            }

            if (!await FinalizeAsync(journal, cancellationToken))
            {
                await _git.RestoreRemoteAfterRejectedSynchronizationPushAsync(journal.BaseRevision, cancellationToken);
                _logger.Warning("Floccus advanced Git during the final push; recalculating the plan from origin/main.");
                continue;
            }

            _logger.Info($"Bidirectional synchronization completed: {journal.AppliedOperations} Raindrop write operations; "
                + $"generation {_states.Parse(journal.FinalStateContent!).Generation}.");
            return;
        }
    }

    private async Task<SynchronizationJournal?> ReconcileJournalAsync(CancellationToken cancellationToken)
    {
        // Do not delete the previous journal until a replacement was fully validated and saved.
        var replacement = await CreateJournalIfChangedAsync(cancellationToken);
        if (replacement is null)
        {
            await _journals.DeleteAsync(cancellationToken);
        }

        return replacement;
    }

    private async Task<SynchronizationJournal?> CreateJournalIfChangedAsync(CancellationToken cancellationToken)
    {
        var stateContent = await _files.ReadStateIfExistsAsync(cancellationToken)
            ?? throw Recovery("The initialized synchronization state is missing.");
        var state = _states.Parse(stateContent);
        var xbelContent = await _files.ReadXbelAsync(cancellationToken);
        var document = _xbel.Parse(xbelContent);
        var revision = await _git.GetCurrentRevisionAsync(cancellationToken);
        var collections = await _raindrop.GetCollectionsAsync(cancellationToken);
        var bookmarks = await _raindrop.GetActiveBookmarksAsync(cancellationToken);
        var comparison = _comparer.Compare(state, document, collections, bookmarks);
        if (!HasChanges(comparison))
        {
            _logger.Info("Both endpoints match the synchronized snapshots; no changes are required.");
            return null;
        }

        var reservedId = state.FolderMappings.Select(item => item.XbelId)
            .Concat(state.BookmarkMappings.Select(item => item.XbelId)).DefaultIfEmpty(0).Max();
        var plan = _planner.CreatePlan(comparison, document with { HighestId = Math.Max(document.HighestId, reservedId) });
        // Preserve original formatting and order when only Raindrop or the baseline changes.
        if (SameTree(plan.XbelTree, comparison.Xbel.Tree)) plan = plan with { XbelContent = xbelContent };
        var journal = new SynchronizationJournal(1, revision, stateContent, xbelContent,
            plan, collections, bookmarks, null, null, 0);
        ValidateJournal(journal);
        await _journals.SaveAsync(journal, cancellationToken);
        var conflicts = comparison.Folders.Count(item => item.IsConflict)
            + comparison.Bookmarks.Count(item => item.IsConflict);
        _logger.Info($"Synchronization plan: Git -> Raindrop ({DescribeTreeChanges(comparison.Raindrop.Tree, plan.RaindropTree)}); "
            + $"Raindrop -> Git ({DescribeTreeChanges(comparison.Xbel.Tree, plan.XbelTree)}); "
            + $"{conflicts} conflict(s) use Git priority.");
        return journal;
    }

    private async Task<SynchronizationJournal?> ApplyAndConfirmPlanAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        if (!await SourceMatchesJournalAfterRefreshAsync(journal, cancellationToken))
        {
            await ReconcileJournalAsync(cancellationToken);
            return null;
        }
        await VerifyRaindropAsync(journal, cancellationToken);
        journal = await ApplyRaindropAsync(journal, cancellationToken);
        await VerifyRaindropAsync(journal, cancellationToken);
        var finalState = CreateState(journal);
        if (HasChanges(_comparer.Compare(finalState, _xbel.Parse(journal.Plan.XbelContent),
                journal.ExpectedCollections, journal.ExpectedBookmarks)))
            throw Recovery("The confirmed Raindrop contents do not match the completed plan.");
        if (!await SourceMatchesJournalAfterRefreshAsync(journal, cancellationToken))
        {
            await ReconcileJournalAsync(cancellationToken);
            return null;
        }
        journal = journal with { FinalStateContent = _states.Serialize(finalState) };
        await _journals.SaveAsync(journal, cancellationToken);
        return journal;
    }

    private async Task<SynchronizationJournal> ApplyRaindropAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        journal = await DetachMovedCollectionsAsync(journal, cancellationToken);
        journal = await DetachObsoleteCollectionsAsync(journal, cancellationToken);
        journal = await ApplyCollectionsAsync(journal, cancellationToken);
        journal = await ApplyBookmarksAsync(journal, cancellationToken);
        journal = await TrashObsoleteBookmarksAsync(journal, cancellationToken);
        return await DeleteObsoleteCollectionsAsync(journal, cancellationToken);
    }

    private async Task<SynchronizationJournal> DetachMovedCollectionsAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        // First detach retained folders that change parents. This makes hierarchy inversion safe:
        // an old ancestor is never moved into its still-attached descendant.
        foreach (var desired in journal.Plan.RaindropTree.Folders)
        {
            var mapping = journal.Plan.Folders.Single(item => item.StableId == desired.Id);
            if (mapping.RaindropId is not { } id) continue;
            var current = journal.ExpectedCollections.Single(item => item.Id == id);
            var targetParent = ResolveRaindropParentId(journal, desired.ParentId);
            if (current.ParentId is not null && (desired.ParentId is not null && targetParent is null
                || current.ParentId != targetParent))
            {
                journal = await WriteRaindropAsync(journal, "detach collection", () => _raindrop.UpdateCollectionAsync(
                    id, new RaindropCollectionWrite(current.Title, null), CancellationToken.None),
                    (currentJournal, result) =>
                        AcceptCollection(currentJournal, result, mapping.StableId, id, null, updateTarget: false), cancellationToken);
            }
        }
        return journal;
    }

    private async Task<SynchronizationJournal> DetachObsoleteCollectionsAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        // Obsolete descendants must not increase the temporary depth of a retained ancestor
        // that is about to move down. Emptying/deletion follows after bookmark evacuation.
        var plannedCollectionIds = journal.Plan.Folders.Where(item => item.RaindropId.HasValue)
            .Select(item => item.RaindropId!.Value).ToHashSet();
        foreach (var obsolete in journal.ExpectedCollections.Where(item => item.ParentId is not null
                     && !plannedCollectionIds.Contains(item.Id)).ToArray())
        {
            journal = await WriteRaindropAsync(journal, "detach obsolete collection", () => _raindrop.UpdateCollectionAsync(
                obsolete.Id, new RaindropCollectionWrite(obsolete.Title, null), CancellationToken.None),
                (currentJournal, result) =>
                    AcceptCollection(currentJournal, result, default, obsolete.Id, null, updateTarget: false), cancellationToken);
        }
        return journal;
    }

    private async Task<SynchronizationJournal> ApplyCollectionsAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        foreach (var desired in OrderFolders(journal.Plan.RaindropTree.Folders))
        {
            var mapping = journal.Plan.Folders.Single(item => item.StableId == desired.Id);
            var parent = ResolveRaindropParentId(journal, desired.ParentId);
            if (desired.ParentId is not null && parent is null)
                throw Recovery("A required collection creation has not been confirmed.");
            if (mapping.RaindropId is not { } id)
            {
                journal = await WriteRaindropAsync(journal, "create collection", () => _raindrop.CreateCollectionAsync(
                    new RaindropCollectionWrite(desired.Title, parent), CancellationToken.None),
                    (currentJournal, result) =>
                        AcceptCollection(currentJournal, result, mapping.StableId, null, parent, updateTarget: true),
                    cancellationToken,
                    () => SynchronizationItemLoggingHelper.Creating(_logger, "folder", desired.Title, "Raindrop"));
            }
            else
            {
                var current = journal.ExpectedCollections.Single(item => item.Id == id);
                if (current.Title == desired.Title && current.ParentId == parent) continue;
                journal = await WriteRaindropAsync(journal, "update collection", () => _raindrop.UpdateCollectionAsync(
                    id, new RaindropCollectionWrite(desired.Title, parent), CancellationToken.None),
                    (currentJournal, result) =>
                        AcceptCollection(currentJournal, result, mapping.StableId, id, parent, updateTarget: true), cancellationToken);
            }
        }
        return journal;
    }

    private async Task<SynchronizationJournal> ApplyBookmarksAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        foreach (var desired in journal.Plan.RaindropTree.Bookmarks)
        {
            var mapping = journal.Plan.Bookmarks.Single(item => item.StableId == desired.Id);
            var collection = desired.ParentId is null ? -1 : ResolveRaindropParentId(journal, desired.ParentId)
                ?? throw Recovery("A destination collection has no confirmed ID.");
            var write = new RaindropBookmarkWrite(collection, desired.Title, desired.Url);
            if (mapping.RaindropId is not { } id)
            {
                journal = await WriteRaindropAsync(journal, "create bookmark", () => CreateBookmarkAsync(write),
                    (currentJournal, result) =>
                        AcceptBookmark(currentJournal, result, mapping.StableId, null, collection),
                    cancellationToken,
                    () => SynchronizationItemLoggingHelper.Creating(_logger, "bookmark", desired.Title, "Raindrop"));
            }
            else
            {
                var current = journal.ExpectedBookmarks.Single(item => item.Id == id);
                if (current.CollectionId == collection && current.Title == desired.Title && current.Link == desired.Url) continue;
                journal = await WriteRaindropAsync(journal, "update bookmark", () => _raindrop.UpdateBookmarkAsync(id, write, CancellationToken.None),
                    (currentJournal, result) =>
                        AcceptBookmark(currentJournal, result, mapping.StableId, id, collection), cancellationToken);
            }
        }
        return journal;
    }

    private async Task<SynchronizationJournal> DeleteObsoleteCollectionsAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        var retainedFolders = journal.Plan.Folders.Select(item => item.RaindropId!.Value).ToHashSet();
        var deletedFolders = journal.ExpectedCollections.Where(item => !retainedFolders.Contains(item.Id)).ToArray();
        var deletedIds = deletedFolders.Select(item => item.Id).ToHashSet();
        while (deletedIds.Count > 0)
        {
            var leaf = FindObsoleteLeaf(journal.ExpectedCollections, deletedIds);
            await VerifyRaindropAsync(journal, cancellationToken);
            if (journal.ExpectedBookmarks.Any(item => item.CollectionId == leaf.Id))
                throw Recovery("A collection scheduled for deletion still contains retained bookmarks.");
            journal = await DeleteCollectionAsync(journal, leaf, cancellationToken);
            deletedIds.Remove(leaf.Id);
        }
        return journal;
    }

    private static RaindropCollection FindObsoleteLeaf(
        IEnumerable<RaindropCollection> collections, ISet<long> deletedIds) =>
        collections.First(item => deletedIds.Contains(item.Id)
            && !collections.Any(child => child.ParentId == item.Id));

    private Task<SynchronizationJournal> DeleteCollectionAsync(SynchronizationJournal journal,
        RaindropCollection collection, CancellationToken cancellationToken) =>
        WriteRaindropAsync(journal, "delete collection", collection.Id, DeleteRaindropCollectionAsync,
            AcceptDeletedCollection, cancellationToken,
            () => SynchronizationItemLoggingHelper.Deleting(_logger, "folder", collection.Title, "Raindrop"));

    private async Task<long> DeleteRaindropCollectionAsync(long collectionId)
    {
        await _raindrop.DeleteCollectionsAsync([collectionId], CancellationToken.None);
        return collectionId;
    }

    private static SynchronizationJournal AcceptDeletedCollection(SynchronizationJournal journal,
        long collectionId) => journal with
    {
        ExpectedCollections = journal.ExpectedCollections.Where(item => item.Id != collectionId).ToArray()
    };

    private static long? ResolveRaindropParentId(SynchronizationJournal journal, StableFolderId? id) =>
        id is { } stableId ? journal.Plan.Folders.Single(item => item.StableId == stableId).RaindropId : null;

    private static SynchronizationJournal AcceptCollection(SynchronizationJournal journal,
        RaindropCollection result, StableFolderId stableId, long? existingId,
        long? parent, bool updateTarget)
    {
        if (result.Id <= 0 || result.Title is null || result.ParentId != parent
            || existingId is { } knownId && result.Id != knownId
            || existingId is null && journal.ExpectedCollections.Any(item => item.Id == result.Id))
            throw Recovery("Raindrop returned an inconsistent collection write response.");
        var plan = journal.Plan;
        if (updateTarget)
        {
            plan = plan with
            {
                Folders = plan.Folders.Select(item => item.StableId == stableId ? item with { RaindropId = result.Id } : item).ToArray(),
                RaindropTree = plan.RaindropTree with
                {
                    Folders = plan.RaindropTree.Folders.Select(item => item.Id == stableId ? item with { Title = result.Title } : item).ToArray()
                }
            };
        }
        return journal with
        {
            Plan = plan,
            ExpectedCollections = journal.ExpectedCollections.Where(item => item.Id != result.Id).Append(result).ToArray()
        };
    }

    private static SynchronizationJournal AcceptBookmark(SynchronizationJournal journal,
        RaindropBookmark result, StableBookmarkId stableId, long? existingId, long collection)
    {
        if (result.Id <= 0 || result.Title is null || string.IsNullOrWhiteSpace(result.Link)
            || result.CollectionId != collection || existingId is { } knownId && result.Id != knownId
            || existingId is null && journal.ExpectedBookmarks.Any(item => item.Id == result.Id))
            throw Recovery("Raindrop returned an inconsistent bookmark write response.");
        var plan = journal.Plan;
        return journal with
        {
            Plan = plan with
            {
                Bookmarks = plan.Bookmarks.Select(item => item.StableId == stableId ? item with { RaindropId = result.Id } : item).ToArray(),
                RaindropTree = plan.RaindropTree with
                {
                    Bookmarks = plan.RaindropTree.Bookmarks.Select(item => item.Id == stableId
                        ? item with { Title = result.Title, Url = result.Link } : item).ToArray()
                }
            },
            ExpectedBookmarks = journal.ExpectedBookmarks.Where(item => item.Id != result.Id).Append(result).ToArray()
        };
    }

    private async Task<SynchronizationJournal> TrashObsoleteBookmarksAsync(
        SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        var retainedBookmarks = journal.Plan.Bookmarks.Select(item => item.RaindropId!.Value).ToHashSet();
        foreach (var group in journal.ExpectedBookmarks.Where(item => !retainedBookmarks.Contains(item.Id))
                     .GroupBy(item => item.CollectionId).ToArray())
        {
            journal = await TrashBookmarkGroupAsync(journal, group, cancellationToken);
        }
        return journal;
    }

    private async Task<SynchronizationJournal> TrashBookmarkGroupAsync(
        SynchronizationJournal journal, IGrouping<long, RaindropBookmark> group,
        CancellationToken cancellationToken)
    {
        foreach (var batch in group.Chunk(100))
        {
            // Check for user moves/edits before a destructive batch, including new children.
            await VerifyRaindropAsync(journal, cancellationToken);
            journal = await TrashBookmarkBatchAsync(journal, group.Key, batch, cancellationToken);
        }
        return journal;
    }

    private Task<SynchronizationJournal> TrashBookmarkBatchAsync(SynchronizationJournal journal,
        long sourceCollectionId, IReadOnlyList<RaindropBookmark> batch, CancellationToken cancellationToken)
    {
        var ids = batch.Select(item => item.Id).ToHashSet();
        return WriteRaindropAsync(journal, "trash bookmarks", (sourceCollectionId, ids),
            TrashRaindropBookmarksAsync, AcceptTrashedBookmarks, cancellationToken, () =>
            {
                foreach (var bookmark in batch)
                {
                    SynchronizationItemLoggingHelper.Deleting(
                        _logger, "bookmark", bookmark.Title, "Raindrop");
                }
            });
    }

    private async Task<HashSet<long>> TrashRaindropBookmarksAsync(
        (long SourceCollectionId, HashSet<long> BookmarkIds) batch)
    {
        await _raindrop.TrashBookmarksAsync(batch.SourceCollectionId, batch.BookmarkIds.ToArray(), CancellationToken.None);
        return batch.BookmarkIds;
    }

    private static SynchronizationJournal AcceptTrashedBookmarks(SynchronizationJournal journal,
        HashSet<long> bookmarkIds) => journal with
    {
        ExpectedBookmarks = journal.ExpectedBookmarks.Where(item => !bookmarkIds.Contains(item.Id)).ToArray()
    };

    private async Task<SynchronizationJournal> WriteRaindropAsync<T>(SynchronizationJournal journal,
        string operation, Func<Task<T>> write, Func<SynchronizationJournal, T, SynchronizationJournal> accept,
        CancellationToken cancellationToken, Action? writeCompleted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        journal = journal with { InFlightOperation = operation };
        await _journals.SaveAsync(journal, cancellationToken);
        // Once intent is durable, finish the bounded API request and persist its response
        // even if shutdown was requested. The next operation checks cancellation again.
        var result = await write();
        journal = accept(journal, result);
        journal = journal with { InFlightOperation = null, AppliedOperations = checked(journal.AppliedOperations + 1) };
        await _journals.SaveAsync(journal, CancellationToken.None);
        writeCompleted?.Invoke();
        return journal;
    }

    private async Task<SynchronizationJournal> WriteRaindropAsync<T, TInput>(SynchronizationJournal journal,
        string operation, TInput input, Func<TInput, Task<T>> write,
        Func<SynchronizationJournal, T, SynchronizationJournal> accept, CancellationToken cancellationToken,
        Action? writeCompleted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        journal = journal with { InFlightOperation = operation };
        await _journals.SaveAsync(journal, cancellationToken);
        var result = await write(input);
        journal = accept(journal, result);
        journal = journal with { InFlightOperation = null, AppliedOperations = checked(journal.AppliedOperations + 1) };
        await _journals.SaveAsync(journal, CancellationToken.None);
        writeCompleted?.Invoke();
        return journal;
    }

    private async Task<RaindropBookmark> CreateBookmarkAsync(RaindropBookmarkWrite write)
    {
        RaindropBookmark? result = null;
        await foreach (var batch in _raindrop.CreateBookmarksAsync([write], CancellationToken.None))
        {
            if (result is not null || batch.Count != 1)
                throw Recovery("A single bookmark creation returned an ambiguous response.");
            result = batch[0];
        }
        return result ?? throw Recovery("Bookmark creation did not return a confirmed ID.");
    }

    private async Task<bool> SourceMatchesJournalAsync(SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        return await _git.GetCurrentRevisionAsync(cancellationToken) == journal.BaseRevision
            && await _files.ReadXbelAsync(cancellationToken) == journal.SourceXbelContent
            && await _files.ReadStateIfExistsAsync(cancellationToken) == journal.BaseStateContent;
    }

    private async Task<bool> FilesAreCompatibleWithFinalizationAsync(SynchronizationJournal journal,
        CancellationToken cancellationToken)
    {
        var stateContent = journal.FinalStateContent
            ?? throw new InvalidOperationException("Finalization compatibility was checked without a final state.");
        var xbelContent = await _files.ReadXbelAsync(cancellationToken);
        var currentState = await _files.ReadStateIfExistsAsync(cancellationToken);
        return (xbelContent == journal.SourceXbelContent || xbelContent == journal.Plan.XbelContent)
            && (currentState == journal.BaseStateContent || currentState == stateContent);
    }

    private async Task<bool> CanResumeFinalizationAsync(SynchronizationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!await FilesAreCompatibleWithFinalizationAsync(journal, cancellationToken))
        {
            return false;
        }

        var currentRevision = await _git.GetCurrentRevisionAsync(cancellationToken);
        return currentRevision == journal.BaseRevision
            || await _git.IsSynchronizationCommitAtHeadAsync(journal.BaseRevision, cancellationToken);
    }

    private async Task<bool> SourceMatchesJournalAfterRefreshAsync(SynchronizationJournal journal,
        CancellationToken cancellationToken)
    {
        return await _git.RefreshAsync(cancellationToken)
            && await SourceMatchesJournalAsync(journal, cancellationToken);
    }

    private async Task VerifyRaindropAsync(SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        var collections = await _raindrop.GetCollectionsAsync(cancellationToken);
        var bookmarks = await _raindrop.GetActiveBookmarksAsync(cancellationToken);
        if (!collections.OrderBy(item => item.Id).SequenceEqual(journal.ExpectedCollections.OrderBy(item => item.Id))
            || !bookmarks.OrderBy(item => item.Id).SequenceEqual(journal.ExpectedBookmarks.OrderBy(item => item.Id)))
            throw Recovery("Raindrop changed outside the confirmed synchronization operations. The pending plan was preserved for reconciliation.");
    }

    private async Task<bool> FinalizeAsync(SynchronizationJournal journal, CancellationToken cancellationToken)
    {
        var stateContent = journal.FinalStateContent ?? throw Recovery("Finalization has no verified state.");
        var currentXbel = await _files.ReadXbelAsync(cancellationToken);
        var currentState = await _files.ReadStateIfExistsAsync(cancellationToken)
            ?? throw Recovery("The state disappeared during synchronization finalization.");
        _xbel.Parse(currentXbel);
        _states.Parse(currentState);
        if (currentXbel != journal.SourceXbelContent && currentXbel != journal.Plan.XbelContent
            || currentState != journal.BaseStateContent && currentState != stateContent)
            throw Recovery("Synchronization files changed during finalization; refusing to overwrite them.");

        // Each rename is atomic; the durable finalization journal makes the pair recoverable.
        if (currentXbel != journal.Plan.XbelContent)
            await _files.WriteXbelAtomicallyAsync(journal.Plan.XbelContent, cancellationToken);
        if (currentState != stateContent)
            await _files.WriteStateAtomicallyAsync(stateContent, cancellationToken);
        await _git.CommitSynchronizationFilesAsync("Synchronize Raindrop and Floccus bookmarks", cancellationToken);
        if (!await _git.PushSynchronizationFilesIfRemoteUnchangedAsync(journal.BaseRevision, cancellationToken))
        {
            return false;
        }
        SynchronizationItemLoggingHelper.LogFloccusCreatesAndDeletes(
            _logger, _xbel.Parse(journal.SourceXbelContent), journal.Plan);
        await _journals.DeleteAsync(cancellationToken);
        return true;
    }

    private SynchronizationState CreateState(SynchronizationJournal journal, bool allowPendingIds = false)
    {
        var baseline = _states.Parse(journal.BaseStateContent);
        // Temporary positive IDs are used only for validating an unexecuted plan, never persisted as state.
        var usedFolders = journal.Plan.Folders.Where(item => item.RaindropId.HasValue).Select(item => item.RaindropId!.Value).ToHashSet();
        var usedBookmarks = journal.Plan.Bookmarks.Where(item => item.RaindropId.HasValue).Select(item => item.RaindropId!.Value).ToHashSet();
        return new SynchronizationState(_states.CurrentSchemaVersion, checked(baseline.Generation + 1), true,
            journal.Plan.XbelTree, journal.Plan.RaindropTree,
            CreateFolderMappings(journal.Plan.Folders, usedFolders, allowPendingIds),
            CreateBookmarkMappings(journal.Plan.Bookmarks, usedBookmarks, allowPendingIds));
    }

    private static FolderIdentityMapping[] CreateFolderMappings(IEnumerable<PendingFolderMapping> mappings,
        HashSet<long> usedIds, bool allowPendingIds) => mappings.Select(item => new FolderIdentityMapping(
            item.StableId, item.XbelId, ResolveRaindropId(item.RaindropId, usedIds, allowPendingIds))).ToArray();

    private static BookmarkIdentityMapping[] CreateBookmarkMappings(IEnumerable<PendingBookmarkMapping> mappings,
        HashSet<long> usedIds, bool allowPendingIds) => mappings.Select(item => new BookmarkIdentityMapping(
            item.StableId, item.XbelId, ResolveRaindropId(item.RaindropId, usedIds, allowPendingIds))).ToArray();

    private static long ResolveRaindropId(long? confirmedId, HashSet<long> usedIds, bool allowPendingIds)
    {
        if (confirmedId is { } id)
        {
            return id;
        }

        if (!allowPendingIds)
        {
            throw Recovery("A final identity mapping has no confirmed Raindrop ID.");
        }

        long temporaryId = 1;
        while (!usedIds.Add(temporaryId))
        {
            temporaryId++;
        }

        return temporaryId;
    }

    private void ValidateJournal(SynchronizationJournal journal)
    {
        if (journal.Version != 1 || string.IsNullOrWhiteSpace(journal.BaseRevision) || journal.AppliedOperations < 0)
            throw Recovery("The synchronization journal has invalid metadata.");
        var baseline = _states.Parse(journal.BaseStateContent);
        var source = _xbel.Parse(journal.SourceXbelContent);
        _comparer.Compare(baseline, source, journal.ExpectedCollections, journal.ExpectedBookmarks);
        var plannedState = CreateState(journal, allowPendingIds: true);
        _states.Validate(plannedState);
        var collections = CreateRaindropCollections(plannedState);
        var bookmarks = CreateRaindropBookmarks(plannedState);
        var comparison = _comparer.Compare(plannedState, _xbel.Parse(journal.Plan.XbelContent),
            collections, bookmarks);
        if (HasChanges(comparison)) throw Recovery("The pending XBEL does not match its planned snapshot.");
        if (journal.FinalStateContent is not null
            && _states.Serialize(_states.Parse(journal.FinalStateContent)) != _states.Serialize(CreateState(journal)))
            throw Recovery("The final state does not match the pending plan.");
    }

    private static IEnumerable<BookmarkTreeFolder> OrderFolders(IReadOnlyList<BookmarkTreeFolder> folders)
    {
        var remaining = folders.ToDictionary(item => item.Id);
        var emitted = new HashSet<StableFolderId>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Values.Where(item => item.ParentId is null || emitted.Contains(item.ParentId.Value)).ToArray();
            if (ready.Length == 0) throw Recovery("The planned collection tree contains a cycle or a missing parent.");
            foreach (var item in ready)
            {
                remaining.Remove(item.Id);
                emitted.Add(item.Id);
                yield return item;
            }
        }
    }

    private static RaindropCollection[] CreateRaindropCollections(SynchronizationState state)
    {
        var folderIds = state.FolderMappings.ToDictionary(item => item.StableId, item => item.RaindropCollectionId);
        return state.RaindropSnapshot.Folders.Select(item => new RaindropCollection(folderIds[item.Id],
            item.ParentId is { } parent ? folderIds[parent] : null, item.Title)).ToArray();
    }

    private static RaindropBookmark[] CreateRaindropBookmarks(SynchronizationState state)
    {
        var folderIds = state.FolderMappings.ToDictionary(item => item.StableId, item => item.RaindropCollectionId);
        var bookmarkIds = state.BookmarkMappings.ToDictionary(item => item.StableId, item => item.RaindropBookmarkId);
        return state.RaindropSnapshot.Bookmarks.Select(item => new RaindropBookmark(bookmarkIds[item.Id],
            item.ParentId is { } parent ? folderIds[parent] : -1, item.Title, item.Url)).ToArray();
    }

    private static bool HasChanges(SynchronizationComparison comparison) =>
        HasEntityChanges(comparison.Folders) || HasEntityChanges(comparison.Bookmarks);

    private static bool HasEntityChanges<TId, TEntity>(IEnumerable<EntityComparison<TId, TEntity>> entities)
        where TId : notnull
        where TEntity : class =>
        entities.Any(item => item.XbelChange != SynchronizationChangeKind.Unchanged
            || item.RaindropChange != SynchronizationChangeKind.Unchanged);

    private static bool SameTree(BookmarkTree left, BookmarkTree right) =>
        left.Folders.OrderBy(item => item.Id.Value).SequenceEqual(right.Folders.OrderBy(item => item.Id.Value))
        && left.Bookmarks.OrderBy(item => item.Id.Value).SequenceEqual(right.Bookmarks.OrderBy(item => item.Id.Value));

    private static string DescribeTreeChanges(BookmarkTree current, BookmarkTree desired) =>
        $"folders {DescribeEntityChanges(current.Folders, desired.Folders, item => item.Id)}; "
        + $"bookmarks {DescribeEntityChanges(current.Bookmarks, desired.Bookmarks, item => item.Id)}";

    private static string DescribeEntityChanges<TId, TEntity>(IReadOnlyList<TEntity> current,
        IReadOnlyList<TEntity> desired, Func<TEntity, TId> getId)
        where TId : notnull
        where TEntity : class
    {
        var currentById = current.ToDictionary(getId);
        var desiredById = desired.ToDictionary(getId);
        var added = desiredById.Keys.Count(id => !currentById.ContainsKey(id));
        var deleted = currentById.Keys.Count(id => !desiredById.ContainsKey(id));
        var modified = desiredById.Count(item => currentById.TryGetValue(item.Key, out var existing)
            && !EqualityComparer<TEntity>.Default.Equals(existing, item.Value));
        var parts = new List<string>(3);
        if (added > 0) parts.Add($"{added} create");
        if (modified > 0) parts.Add($"{modified} update");
        if (deleted > 0) parts.Add($"{deleted} delete");
        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    private static SynchronizationRecoveryRequiredException Recovery(string message) => new(
        message + " Keep the persistent working volume and .git/raindrop-sync-pending.json; do not delete the state or restart initialization.");
}
