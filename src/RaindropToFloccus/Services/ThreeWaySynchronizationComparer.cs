namespace RaindropToFloccus.Services;

public sealed class ThreeWaySynchronizationComparer : ISynchronizationComparer
{
    private const long UnsortedCollectionId = -1;
    private const int MaximumFolderDepth = 128;

    private readonly ISynchronizationStateSerializer _stateSerializer;
    private readonly IXbelDocumentSerializer _xbelSerializer;

    public ThreeWaySynchronizationComparer(
        ISynchronizationStateSerializer stateSerializer,
        IXbelDocumentSerializer xbelSerializer)
    {
        _stateSerializer = stateSerializer;
        _xbelSerializer = xbelSerializer;
    }

    public SynchronizationComparison Compare(
        SynchronizationState state,
        XbelDocument xbel,
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> bookmarks)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(bookmarks);
        _stateSerializer.Validate(state);
        _xbelSerializer.Validate(xbel);
        ValidateRaindropSnapshot(collections, bookmarks);

        var usedIds = state.FolderMappings.Select(mapping => mapping.StableId.Value)
            .Concat(state.BookmarkMappings.Select(mapping => mapping.StableId.Value)).ToHashSet();
        var currentXbel = CreateXbelTree(state, xbel, usedIds);
        var currentRaindrop = CreateRaindropTree(state, collections, bookmarks, usedIds);

        return new SynchronizationComparison(
            state.Generation,
            currentXbel,
            currentRaindrop,
            CompareEntities(
                state.XbelSnapshot.Folders, state.RaindropSnapshot.Folders,
                currentXbel.Tree.Folders, currentRaindrop.Tree.Folders,
                folder => folder.Id, id => id.Value),
            CompareEntities(
                state.XbelSnapshot.Bookmarks, state.RaindropSnapshot.Bookmarks,
                currentXbel.Tree.Bookmarks, currentRaindrop.Tree.Bookmarks,
                bookmark => bookmark.Id, id => id.Value));
    }

    private static SourceBookmarkTree CreateXbelTree(
        SynchronizationState state, XbelDocument document, ISet<Guid> usedIds)
    {
        var knownFolders = state.FolderMappings.ToDictionary(mapping => mapping.XbelId, mapping => mapping.StableId);
        var knownBookmarks = state.BookmarkMappings.ToDictionary(mapping => mapping.XbelId, mapping => mapping.StableId);
        var folders = new List<BookmarkTreeFolder>();
        var bookmarks = new List<BookmarkTreeBookmark>();
        var folderSourceIds = new Dictionary<StableFolderId, long>();
        var bookmarkSourceIds = new Dictionary<StableBookmarkId, long>();

        AddItems(document.Items, null);
        return CreateSourceTree(folders, bookmarks, folderSourceIds, bookmarkSourceIds);

        void AddItems(IReadOnlyList<XbelItem> items, StableFolderId? parentId)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case XbelFolder folder:
                        if (knownBookmarks.ContainsKey(folder.Id))
                        {
                            throw new XbelFormatException("An existing XBEL bookmark ID was reused for a folder.");
                        }

                        var folderId = knownFolders.TryGetValue(folder.Id, out var knownFolderId)
                            ? knownFolderId : new StableFolderId(CreateStableId(usedIds));
                        folderSourceIds.Add(folderId, folder.Id);
                        folders.Add(new BookmarkTreeFolder(folderId, parentId, folder.Title));
                        AddItems(folder.Children, folderId);
                        break;
                    case XbelBookmark bookmark:
                        if (knownFolders.ContainsKey(bookmark.Id))
                        {
                            throw new XbelFormatException("An existing XBEL folder ID was reused for a bookmark.");
                        }

                        var bookmarkId = knownBookmarks.TryGetValue(bookmark.Id, out var knownBookmarkId)
                            ? knownBookmarkId : new StableBookmarkId(CreateStableId(usedIds));
                        bookmarkSourceIds.Add(bookmarkId, bookmark.Id);
                        bookmarks.Add(new BookmarkTreeBookmark(bookmarkId, parentId, bookmark.Title, bookmark.Url));
                        break;
                    default:
                        throw new XbelFormatException("The XBEL tree contains an unsupported item type.");
                }
            }
        }
    }

    private static SourceBookmarkTree CreateRaindropTree(
        SynchronizationState state,
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> sourceBookmarks,
        ISet<Guid> usedIds)
    {
        var knownFolders = state.FolderMappings.ToDictionary(
            mapping => mapping.RaindropCollectionId, mapping => mapping.StableId);
        var knownBookmarks = state.BookmarkMappings.ToDictionary(
            mapping => mapping.RaindropBookmarkId, mapping => mapping.StableId);

        // Allocate all current folder identities first, so input order has no structural meaning.
        var currentFolderIds = new Dictionary<long, StableFolderId>();
        foreach (var collection in collections)
        {
            var id = knownFolders.TryGetValue(collection.Id, out var knownId)
                ? knownId : new StableFolderId(CreateStableId(usedIds));
            currentFolderIds.Add(collection.Id, id);
        }

        var folders = new List<BookmarkTreeFolder>();
        foreach (var collection in collections)
        {
            folders.Add(new BookmarkTreeFolder(
                currentFolderIds[collection.Id],
                collection.ParentId is { } parentId ? currentFolderIds[parentId] : null,
                collection.Title));
        }

        var bookmarks = new List<BookmarkTreeBookmark>();
        var bookmarkSourceIds = new Dictionary<StableBookmarkId, long>();
        foreach (var bookmark in sourceBookmarks)
        {
            var id = knownBookmarks.TryGetValue(bookmark.Id, out var knownId)
                ? knownId : new StableBookmarkId(CreateStableId(usedIds));
            bookmarkSourceIds.Add(id, bookmark.Id);
            bookmarks.Add(new BookmarkTreeBookmark(
                id,
                bookmark.CollectionId == UnsortedCollectionId ? null : currentFolderIds[bookmark.CollectionId],
                bookmark.Title,
                bookmark.Link));
        }

        return CreateSourceTree(
            folders, bookmarks,
            currentFolderIds.ToDictionary(pair => pair.Value, pair => pair.Key), bookmarkSourceIds);
    }

    private static SourceBookmarkTree CreateSourceTree(
        List<BookmarkTreeFolder> folders,
        List<BookmarkTreeBookmark> bookmarks,
        Dictionary<StableFolderId, long> folderSourceIds,
        Dictionary<StableBookmarkId, long> bookmarkSourceIds)
    {
        return new SourceBookmarkTree(
            new BookmarkTree(
                Array.AsReadOnly(folders.OrderBy(item => item.Id.Value).ToArray()),
                Array.AsReadOnly(bookmarks.OrderBy(item => item.Id.Value).ToArray())),
            new ReadOnlyDictionary<StableFolderId, long>(folderSourceIds),
            new ReadOnlyDictionary<StableBookmarkId, long>(bookmarkSourceIds));
    }

    private static IReadOnlyList<EntityComparison<TId, TEntity>> CompareEntities<TId, TEntity>(
        IReadOnlyList<TEntity> previousXbel,
        IReadOnlyList<TEntity> previousRaindrop,
        IReadOnlyList<TEntity> currentXbel,
        IReadOnlyList<TEntity> currentRaindrop,
        Func<TEntity, TId> getId,
        Func<TId, Guid> getSortKey)
        where TId : notnull
        where TEntity : class
    {
        var xbelBaseline = previousXbel.ToDictionary(getId);
        var raindropBaseline = previousRaindrop.ToDictionary(getId);
        var xbel = currentXbel.ToDictionary(getId);
        var raindrop = currentRaindrop.ToDictionary(getId);
        var ids = xbelBaseline.Keys.Concat(raindropBaseline.Keys)
            .Concat(xbel.Keys).Concat(raindrop.Keys).Distinct().OrderBy(getSortKey);
        var comparisons = new List<EntityComparison<TId, TEntity>>();
        foreach (var id in ids)
        {
            var oldXbel = xbelBaseline.GetValueOrDefault(id);
            var oldRaindrop = raindropBaseline.GetValueOrDefault(id);
            var newXbel = xbel.GetValueOrDefault(id);
            var newRaindrop = raindrop.GetValueOrDefault(id);
            var xbelChange = GetChange(oldXbel, newXbel);
            var raindropChange = GetChange(oldRaindrop, newRaindrop);
            comparisons.Add(new EntityComparison<TId, TEntity>(
                id, oldXbel, oldRaindrop, newXbel, newRaindrop, xbelChange, raindropChange,
                xbelChange != SynchronizationChangeKind.Unchanged
                    && raindropChange != SynchronizationChangeKind.Unchanged
                    && !EqualityComparer<TEntity>.Default.Equals(newXbel, newRaindrop)));
        }

        return comparisons.AsReadOnly();
    }

    private static SynchronizationChangeKind GetChange<TEntity>(TEntity? previous, TEntity? current)
        where TEntity : class
    {
        if (EqualityComparer<TEntity>.Default.Equals(previous, current))
        {
            return SynchronizationChangeKind.Unchanged;
        }

        if (previous is null)
        {
            return SynchronizationChangeKind.Added;
        }

        return current is null ? SynchronizationChangeKind.Deleted : SynchronizationChangeKind.Modified;
    }

    private static Guid CreateStableId(ISet<Guid> usedIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (id == Guid.Empty || !usedIds.Add(id));

        return id;
    }

    private static void ValidateCollectionAncestry(RaindropCollection collection,
        IReadOnlyDictionary<long, RaindropCollection> folders)
    {
        var current = collection;
        var ancestors = new HashSet<long> { collection.Id };
        while (current.ParentId is { } parentId)
        {
            if (!folders.TryGetValue(parentId, out var parent))
            {
                throw InvalidRaindropSnapshot("A collection parent is missing.");
            }

            if (!ancestors.Add(parentId) || ancestors.Count > MaximumFolderDepth)
            {
                throw InvalidRaindropSnapshot("The collection tree contains a cycle or exceeds the depth limit.");
            }

            current = parent;
        }
    }

    private static void ValidateRaindropSnapshot(
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> bookmarks)
    {
        var folders = new Dictionary<long, RaindropCollection>();
        foreach (var collection in collections)
        {
            if (collection is null || collection.Id <= 0 || collection.Title is null
                || !folders.TryAdd(collection.Id, collection))
            {
                throw InvalidRaindropSnapshot("Invalid or duplicate user collection.");
            }
        }

        foreach (var collection in collections)
        {
            ValidateCollectionAncestry(collection, folders);
        }

        var bookmarkIds = new HashSet<long>();
        foreach (var bookmark in bookmarks)
        {
            if (bookmark is null || bookmark.Id <= 0 || bookmark.Title is null
                || string.IsNullOrWhiteSpace(bookmark.Link) || !bookmarkIds.Add(bookmark.Id)
                || bookmark.CollectionId != UnsortedCollectionId && !folders.ContainsKey(bookmark.CollectionId))
            {
                throw InvalidRaindropSnapshot("Invalid or duplicate active bookmark, or its collection is missing.");
            }
        }
    }

    private static RaindropApiException InvalidRaindropSnapshot(string reason)
    {
        return new RaindropApiException("compare snapshot", reason, isTransient: true);
    }
}
