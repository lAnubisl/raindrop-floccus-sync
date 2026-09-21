namespace RaindropToFloccus.Services;

public sealed class GitPrioritySynchronizationPlanner : ISynchronizationPlanner
{
    private const long MaximumFloccusId = 9_007_199_254_740_991;
    private readonly IXbelDocumentSerializer _xbelSerializer;

    public GitPrioritySynchronizationPlanner(IXbelDocumentSerializer xbelSerializer)
    {
        _xbelSerializer = xbelSerializer;
    }

    public SynchronizationPlan CreatePlan(SynchronizationComparison comparison, XbelDocument source)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        _xbelSerializer.Validate(source);
        var gitFolders = comparison.Xbel.Tree.Folders.ToDictionary(item => item.Id);
        var gitBookmarks = comparison.Xbel.Tree.Bookmarks.ToDictionary(item => item.Id);
        var (xbelFolders, raindropFolders) = SelectEntities(comparison.Folders);
        var (xbelBookmarks, raindropBookmarks) = SelectEntities(comparison.Bookmarks);

        ResolveMissingAncestors(gitFolders, gitBookmarks, xbelFolders, raindropFolders, xbelBookmarks, raindropBookmarks);
        ResolveInvalidAncestry(gitFolders, xbelFolders, raindropFolders);

        return CreateMappedPlan(comparison, source,
            new BookmarkTree(xbelFolders.Values.ToArray(), xbelBookmarks.Values.ToArray()),
            new BookmarkTree(raindropFolders.Values.ToArray(), raindropBookmarks.Values.ToArray()));
    }

    private static (Dictionary<TId, TEntity> Xbel, Dictionary<TId, TEntity> Raindrop)
        SelectEntities<TId, TEntity>(IEnumerable<EntityComparison<TId, TEntity>> comparisons)
        where TId : notnull where TEntity : class
    {
        var xbelEntities = new Dictionary<TId, TEntity>();
        var raindropEntities = new Dictionary<TId, TEntity>();
        foreach (var item in comparisons)
        {
            var (xbel, raindrop) = Select(item);
            if (xbel is not null && raindrop is not null)
            {
                xbelEntities.Add(item.Id, xbel);
                raindropEntities.Add(item.Id, raindrop);
            }
        }
        return (xbelEntities, raindropEntities);
    }

    private static void ResolveMissingAncestors(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        IReadOnlyDictionary<StableBookmarkId, BookmarkTreeBookmark> gitBookmarks,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders,
        Dictionary<StableBookmarkId, BookmarkTreeBookmark> xbelBookmarks,
        Dictionary<StableBookmarkId, BookmarkTreeBookmark> raindropBookmarks)
    {
        // Resolve missing ancestors without resurrecting a parent deleted in Git.
        bool changed;
        do
        {
            changed = false;
            changed |= ResolveMissingFolderParents(gitFolders, xbelFolders, raindropFolders);

            changed |= ResolveMissingBookmarkParents(gitFolders, xbelFolders, raindropFolders, gitBookmarks, xbelBookmarks, raindropBookmarks);
        } while (changed);
    }

    private static void ResolveInvalidAncestry(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        // Independent valid moves can form a cycle or a tree deeper than either source.
        // Restore Git versions of involved existing folders first. If only a new Raindrop
        // subtree exceeds the limit, promote its highest new ancestor to root, preserving it.
        while (FindInvalidAncestry(xbelFolders) is { } invalid)
        {
            var restored = RestoreConflictingGitFolders(invalid, gitFolders, xbelFolders, raindropFolders);

            if (!restored)
            {
                var newId = invalid.LastOrDefault(id => !gitFolders.ContainsKey(id));
                if (newId == default)
                    throw new InvalidOperationException("Cannot resolve the merged folder structure.");
                xbelFolders[newId] = xbelFolders[newId] with { ParentId = null };
                raindropFolders[newId] = raindropFolders[newId] with { ParentId = null };
            }
        }
    }

    private SynchronizationPlan CreateMappedPlan(SynchronizationComparison comparison, XbelDocument source,
        BookmarkTree xbelTree, BookmarkTree raindropTree)
    {
        var highestId = source.HighestId;
        var folders = CreateFolderMappings(comparison, xbelTree.Folders, ref highestId);
        var bookmarks = CreateBookmarkMappings(comparison, xbelTree.Bookmarks, ref highestId);
        var folderIds = folders.ToDictionary(item => item.StableId, item => item.XbelId);
        var bookmarkIds = bookmarks.ToDictionary(item => item.StableId, item => item.XbelId);
        var sourceOrder = CreateSourceOrder(source.Items);
        var content = _xbelSerializer.Serialize(new XbelDocument(highestId,
            BuildItems(xbelTree, folderIds, bookmarkIds, sourceOrder, null)));

        if (raindropTree.Bookmarks.Any(bookmark => bookmark.Title.Length > 1000))
        {
            throw new XbelFormatException(
                "A planned bookmark title exceeds the Raindrop limit of 1000 characters.");
        }

        return new SynchronizationPlan(
            xbelTree,
            raindropTree,
            folders, bookmarks, content);
    }

    private static PendingFolderMapping[] CreateFolderMappings(SynchronizationComparison comparison,
        IEnumerable<BookmarkTreeFolder> folders, ref long highestId)
    {
        var mappings = new List<PendingFolderMapping>();
        foreach (var folder in folders.OrderBy(item => item.Id.Value))
        {
            var id = folder.Id;
            var xbelId = comparison.Xbel.FolderSourceIds.TryGetValue(id, out var sourceId)
                ? sourceId : NextId(ref highestId);
            long? raindropId = comparison.Raindrop.FolderSourceIds.TryGetValue(id, out var knownRaindropId)
                ? knownRaindropId : null;
            mappings.Add(new PendingFolderMapping(id, xbelId, raindropId));
        }

        return mappings.ToArray();
    }

    private static PendingBookmarkMapping[] CreateBookmarkMappings(SynchronizationComparison comparison,
        IEnumerable<BookmarkTreeBookmark> bookmarks, ref long highestId)
    {
        var mappings = new List<PendingBookmarkMapping>();
        foreach (var bookmark in bookmarks.OrderBy(item => item.Id.Value))
        {
            var id = bookmark.Id;
            var xbelId = comparison.Xbel.BookmarkSourceIds.TryGetValue(id, out var sourceId)
                ? sourceId : NextId(ref highestId);
            long? raindropId = comparison.Raindrop.BookmarkSourceIds.TryGetValue(id, out var knownRaindropId)
                ? knownRaindropId : null;
            mappings.Add(new PendingBookmarkMapping(id, xbelId, raindropId));
        }

        return mappings.ToArray();
    }

    private static long NextId(ref long highestId)
    {
        if (highestId >= MaximumFloccusId)
            throw new XbelFormatException("The Floccus ID space is exhausted.");
        return ++highestId;
    }

    private static IReadOnlyDictionary<long, int> CreateSourceOrder(IReadOnlyList<XbelItem> items)
    {
        var order = new Dictionary<long, int>();
        AddItems(items);
        return order;

        void AddItems(IEnumerable<XbelItem> sourceItems)
        {
            foreach (var item in sourceItems)
            {
                order.Add(item.Id, order.Count);
                if (item is XbelFolder folder) AddItems(folder.Children);
            }
        }
    }

    private static IReadOnlyList<XbelItem> BuildItems(BookmarkTree tree,
        IReadOnlyDictionary<StableFolderId, long> folderIds,
        IReadOnlyDictionary<StableBookmarkId, long> bookmarkIds,
        IReadOnlyDictionary<long, int> sourceOrder,
        StableFolderId? parent)
    {
        var items = new List<XbelItem>();
        foreach (var folder in tree.Folders.Where(item => item.ParentId == parent))
        {
            items.Add(new XbelFolder(
                folderIds[folder.Id],
                folder.Title,
                BuildItems(tree, folderIds, bookmarkIds, sourceOrder, folder.Id)));
        }

        foreach (var bookmark in tree.Bookmarks.Where(item => item.ParentId == parent))
        {
            items.Add(new XbelBookmark(bookmarkIds[bookmark.Id], bookmark.Title, bookmark.Url));
        }

        return items.OrderBy(item => sourceOrder.GetValueOrDefault(item.Id, int.MaxValue))
            .ThenBy(item => item.Id)
            .ToArray();
    }

    private static bool ResolveMissingFolderParents(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        var changed = false;
        foreach (var folder in xbelFolders.Values.ToArray())
        {
            if (folder.ParentId is not { } parent || xbelFolders.ContainsKey(parent)) continue;
            if (gitFolders.TryGetValue(parent, out var gitParent))
            {
                UseGitFolder(gitParent, xbelFolders, raindropFolders);
            }
            else if (gitFolders.TryGetValue(folder.Id, out var gitFolder))
            {
                UseGitFolder(gitFolder, xbelFolders, raindropFolders);
            }
            else
            {
                xbelFolders.Remove(folder.Id);
                raindropFolders.Remove(folder.Id);
            }
            changed = true;
        }
        return changed;
    }

    private static bool ResolveMissingBookmarkParents(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders,
        IReadOnlyDictionary<StableBookmarkId, BookmarkTreeBookmark> gitBookmarks,
        Dictionary<StableBookmarkId, BookmarkTreeBookmark> xbelBookmarks,
        Dictionary<StableBookmarkId, BookmarkTreeBookmark> raindropBookmarks)
    {
        var changed = false;
        foreach (var bookmark in xbelBookmarks.Values.ToArray())
        {
            if (bookmark.ParentId is not { } parent || xbelFolders.ContainsKey(parent)) continue;
            if (gitFolders.TryGetValue(parent, out var gitParent))
            {
                UseGitFolder(gitParent, xbelFolders, raindropFolders);
            }
            else if (gitBookmarks.TryGetValue(bookmark.Id, out var gitBookmark))
            {
                xbelBookmarks[bookmark.Id] = gitBookmark;
                raindropBookmarks[bookmark.Id] = gitBookmark;
            }
            else
            {
                xbelBookmarks.Remove(bookmark.Id);
                raindropBookmarks.Remove(bookmark.Id);
            }
            changed = true;
        }
        return changed;
    }

    private static bool RestoreConflictingGitFolders(IReadOnlyList<StableFolderId> invalid,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        var restored = false;
        foreach (var id in invalid)
        {
            if (gitFolders.TryGetValue(id, out var gitFolder)
                && xbelFolders[id].ParentId != gitFolder.ParentId)
            {
                UseGitFolder(gitFolder, xbelFolders, raindropFolders);
                RestoreGitAncestors(gitFolder.ParentId, gitFolders, xbelFolders, raindropFolders);
                restored = true;
            }
        }
        return restored;
    }

    private static void UseGitFolder(BookmarkTreeFolder folder,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        xbelFolders[folder.Id] = folder;
        raindropFolders[folder.Id] = folder;
    }

    private static void RestoreGitAncestors(StableFolderId? parent,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> gitFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        Dictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        while (parent is { } id)
        {
            var folder = gitFolders[id];
            if (!xbelFolders.ContainsKey(id)) UseGitFolder(folder, xbelFolders, raindropFolders);
            parent = folder.ParentId;
        }
    }

    private static IReadOnlyList<StableFolderId>? FindInvalidAncestorChain(StableFolderId folderId,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> folders)
    {
        var chain = new List<StableFolderId>();
        var visited = new HashSet<StableFolderId>();
        StableFolderId? current = folderId;
        while (current is { } id)
        {
            if (!visited.Add(id)) return chain;
            chain.Add(id);
            if (chain.Count > 128) return chain;
            current = folders[id].ParentId;
        }
        return null;
    }

    private static (TEntity? Xbel, TEntity? Raindrop) Select<TId, TEntity>(EntityComparison<TId, TEntity> item)
        where TId : notnull where TEntity : class
    {
        if (item.XbelChange != SynchronizationChangeKind.Unchanged)
            return (item.CurrentXbel, item.CurrentXbel);
        if (item.RaindropChange != SynchronizationChangeKind.Unchanged)
            return (item.CurrentRaindrop, item.CurrentRaindrop);
        return (item.CurrentXbel, item.CurrentRaindrop);
    }

    private static IReadOnlyList<StableFolderId>? FindInvalidAncestry(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> folders)
    {
        foreach (var folder in folders.Values.OrderBy(item => item.Id.Value))
        {
            var invalid = FindInvalidAncestorChain(folder.Id, folders);
            if (invalid is not null) return invalid;
        }
        return null;
    }
}
