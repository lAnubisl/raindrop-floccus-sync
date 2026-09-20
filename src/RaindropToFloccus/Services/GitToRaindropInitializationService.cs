using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Services;

public sealed class GitToRaindropInitializationService : IInitialSynchronizationService
{
    private const long UnsortedCollectionId = -1;
    private const int MaximumRaindropBookmarkTitleLength = 1000;
    private const string InitializationCommitMessage = "Initialize Raindrop synchronization state";
    private const string RecoveryCommitMessage = "Recover Raindrop synchronization state";

    private readonly IGitRepositoryClient _gitRepositoryClient;
    private readonly ISynchronizationFileStore _fileStore;
    private readonly IXbelDocumentSerializer _xbelSerializer;
    private readonly ISynchronizationStateSerializer _stateSerializer;
    private readonly IRaindropClient _raindropClient;
    private readonly ApplicationLogger _logger;

    public GitToRaindropInitializationService(
        IGitRepositoryClient gitRepositoryClient,
        ISynchronizationFileStore fileStore,
        IXbelDocumentSerializer xbelSerializer,
        ISynchronizationStateSerializer stateSerializer,
        IRaindropClient raindropClient,
        ApplicationLogger logger)
    {
        _gitRepositoryClient = gitRepositoryClient;
        _fileStore = fileStore;
        _xbelSerializer = xbelSerializer;
        _stateSerializer = stateSerializer;
        _raindropClient = raindropClient;
        _logger = logger;
    }

    public async Task<bool> InitializeIfRequiredAsync(CancellationToken cancellationToken = default)
    {
        await _gitRepositoryClient.InitializeAsync(cancellationToken);
        await _gitRepositoryClient.RefreshAsync(cancellationToken);

        var stateContent = await _fileStore.ReadStateIfExistsAsync(cancellationToken);
        if (stateContent is not null)
        {
            await RecoverExistingStateAsync(stateContent, cancellationToken);
            return false;
        }

        // Parse and validate the complete source before the first destructive Raindrop operation.
        var xbelDocument = _xbelSerializer.Parse(await _fileStore.ReadXbelAsync(cancellationToken));
        var sourceRevision = await _gitRepositoryClient.GetCurrentRevisionAsync(cancellationToken);
        var sourceTree = CreateSourceTree(xbelDocument);
        ValidateRaindropInputs(sourceTree);

        _logger.Info(
            $"Starting initial Git to Raindrop import of {sourceTree.XbelTree.Folders.Count} folders "
            + $"and {sourceTree.XbelTree.Bookmarks.Count} bookmarks.");

        await ClearActiveRaindropContentAsync(cancellationToken);

        _logger.Info($"Initial import Git -> Raindrop: creating {sourceTree.Folders.Count} folders.");
        var folderMappings = await CreateCollectionsAsync(sourceTree, cancellationToken);
        _logger.Info($"Initial import Git -> Raindrop: created {folderMappings.Count} folders.");

        _logger.Info($"Initial import Git -> Raindrop: creating {sourceTree.Bookmarks.Count} bookmarks.");
        var bookmarkMappings = await CreateBookmarksAsync(sourceTree, folderMappings, cancellationToken);
        var raindropTree = await ReadAndValidateImportedTreeAsync(
            sourceTree,
            folderMappings,
            bookmarkMappings,
            cancellationToken);

        await VerifyImportSourceUnchangedAsync(sourceRevision, cancellationToken);

        await SaveInitialStateAsync(sourceTree, raindropTree, folderMappings, bookmarkMappings, cancellationToken);
        _logger.Info(
            $"Initial Git to Raindrop import completed: {folderMappings.Count} folders and "
            + $"{bookmarkMappings.Count} bookmarks synchronized.");
        return true;
    }

    private async Task RecoverExistingStateAsync(string stateContent, CancellationToken cancellationToken)
    {
        _stateSerializer.Parse(stateContent);
        _xbelSerializer.Parse(await _fileStore.ReadXbelAsync(cancellationToken));
        var recoveredCommit = await _gitRepositoryClient.CommitSynchronizationFilesAsync(
            RecoveryCommitMessage,
            cancellationToken);
        await _gitRepositoryClient.PushAsync(cancellationToken);
        if (recoveredCommit)
        {
            _logger.Info("Recovered and pushed an initialization state left by an interrupted finalization.");
        }

        _logger.Info("A valid synchronization state already exists; initial import is not required.");
    }

    private async Task VerifyImportSourceUnchangedAsync(string sourceRevision, CancellationToken cancellationToken)
    {
        // Do not bind a state snapshot to an XBEL revision that changed during the import.
        await _gitRepositoryClient.RefreshAsync(cancellationToken);
        var finalRevision = await _gitRepositoryClient.GetCurrentRevisionAsync(cancellationToken);
        if (!string.Equals(sourceRevision, finalRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Git revision changed during initial import; initialization must restart from the new XBEL.");
        }

        if (await _fileStore.ReadStateIfExistsAsync(cancellationToken) is not null)
        {
            throw new InvalidOperationException(
                "A synchronization state appeared during initial import; refusing to overwrite it.");
        }
    }

    private async Task SaveInitialStateAsync(InitialSynchronizationSourceTree sourceTree, BookmarkTree raindropTree,
        IReadOnlyList<FolderIdentityMapping> folderMappings,
        IReadOnlyList<BookmarkIdentityMapping> bookmarkMappings, CancellationToken cancellationToken)
    {
        var state = new SynchronizationState(
            _stateSerializer.CurrentSchemaVersion,
            Generation: 1,
            InitializationCompleted: true,
            sourceTree.XbelTree,
            raindropTree,
            folderMappings,
            bookmarkMappings);
        var serializedState = _stateSerializer.Serialize(state);
        await _fileStore.WriteNewStateAtomicallyAsync(serializedState, cancellationToken);

        var committed = await _gitRepositoryClient.CommitSynchronizationFilesAsync(
            InitializationCommitMessage,
            cancellationToken);
        if (!committed)
        {
            throw new InvalidOperationException(
                "The new synchronization state did not produce a Git commit.");
        }

        await _gitRepositoryClient.PushAsync(cancellationToken);
    }

    private static void ValidateImportedMembership(IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> bookmarks,
        IReadOnlyDictionary<long, FolderIdentityMapping> folderMappingByRaindropId,
        IReadOnlyDictionary<long, BookmarkIdentityMapping> bookmarkMappingByRaindropId)
    {
        if (collections.Count != folderMappingByRaindropId.Count
            || bookmarks.Count != bookmarkMappingByRaindropId.Count
            || !collections.Select(collection => collection.Id).ToHashSet()
                .SetEquals(folderMappingByRaindropId.Keys)
            || !bookmarks.Select(bookmark => bookmark.Id).ToHashSet()
                .SetEquals(bookmarkMappingByRaindropId.Keys))
        {
            throw new InvalidOperationException(
                "The Raindrop contents changed or were incomplete while initial import was being verified.");
        }
    }

    private static IReadOnlyList<BookmarkTreeFolder> CreateImportedFolders(InitialSynchronizationSourceTree sourceTree,
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyDictionary<long, FolderIdentityMapping> folderMappingByRaindropId,
        IReadOnlyDictionary<long, StableFolderId> stableFolderIdByRaindropId)
    {
        var raindropFolders = new List<BookmarkTreeFolder>(collections.Count);
        foreach (var collection in collections)
        {
            var mapping = folderMappingByRaindropId[collection.Id];
            var expectedSourceFolder = sourceTree.FolderByXbelId[mapping.XbelId];
            StableFolderId? expectedParentId = expectedSourceFolder.ParentXbelId is { } parentXbelId
                ? sourceTree.StableFolderIdByXbelId[parentXbelId]
                : null;
            var actualParentId = collection.ParentId is { } parentRaindropId
                && stableFolderIdByRaindropId.TryGetValue(parentRaindropId, out var stableParentId)
                    ? stableParentId
                    : (StableFolderId?)null;
            if (collection.ParentId is not null && actualParentId is null
                || actualParentId != expectedParentId)
            {
                throw new InvalidOperationException(
                    "A Raindrop collection has an unexpected parent after initial import.");
            }

            raindropFolders.Add(new BookmarkTreeFolder(
                mapping.StableId,
                actualParentId,
                collection.Title));
        }
        return raindropFolders.AsReadOnly();
    }

    private static IReadOnlyList<BookmarkTreeBookmark> CreateImportedBookmarks(InitialSynchronizationSourceTree sourceTree,
        IReadOnlyList<RaindropBookmark> bookmarks,
        IReadOnlyDictionary<long, BookmarkIdentityMapping> bookmarkMappingByRaindropId,
        IReadOnlyDictionary<long, StableFolderId> stableFolderIdByRaindropId)
    {
        var raindropBookmarks = new List<BookmarkTreeBookmark>(bookmarks.Count);
        foreach (var bookmark in bookmarks)
        {
            var mapping = bookmarkMappingByRaindropId[bookmark.Id];
            var expectedSourceBookmark = sourceTree.BookmarkByXbelId[mapping.XbelId];
            StableFolderId? expectedParentId = expectedSourceBookmark.ParentXbelId is { } parentXbelId
                ? sourceTree.StableFolderIdByXbelId[parentXbelId]
                : null;
            StableFolderId? actualParentId;
            if (bookmark.CollectionId == UnsortedCollectionId)
            {
                actualParentId = null;
            }
            else if (stableFolderIdByRaindropId.TryGetValue(bookmark.CollectionId, out var stableParentId))
            {
                actualParentId = stableParentId;
            }
            else
            {
                throw new InvalidOperationException(
                    "A Raindrop bookmark belongs to an unknown collection after initial import.");
            }

            if (actualParentId != expectedParentId)
            {
                throw new InvalidOperationException(
                    "A Raindrop bookmark has an unexpected parent after initial import.");
            }

            raindropBookmarks.Add(new BookmarkTreeBookmark(
                mapping.StableId,
                actualParentId,
                bookmark.Title,
                bookmark.Link));
        }
        return raindropBookmarks.AsReadOnly();
    }

    private async Task ClearActiveRaindropContentAsync(CancellationToken cancellationToken)
    {
        var collections = await _raindropClient.GetCollectionsAsync(cancellationToken);
        var bookmarks = await _raindropClient.GetActiveBookmarksAsync(cancellationToken);
        ValidateRaindropSnapshot(collections, bookmarks);

        foreach (var group in bookmarks
                     .GroupBy(bookmark => bookmark.CollectionId)
                     .OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _raindropClient.TrashBookmarksAsync(
                group.Key,
                group.ToArray(),
                cancellationToken);
        }

        if (collections.Count > 0)
        {
            var depths = GetCollectionDepths(collections);
            var deletedCollections = collections
                .OrderByDescending(collection => depths[collection.Id])
                .ThenBy(collection => collection.Id)
                .ToArray();
            await _raindropClient.DeleteCollectionsAsync(deletedCollections, cancellationToken);
        }

        var remainingBookmarks = await _raindropClient.GetActiveBookmarksAsync(cancellationToken);
        var remainingCollections = await _raindropClient.GetCollectionsAsync(cancellationToken);
        if (remainingBookmarks.Count != 0 || remainingCollections.Count != 0)
        {
            throw new InvalidOperationException(
                "Raindrop still contains active bookmarks or user collections after initial cleanup.");
        }

        _logger.Info(
            $"Cleared {bookmarks.Count} active Raindrop bookmarks and {collections.Count} user collections; "
            + "existing Trash contents were preserved.");
    }

    private async Task<IReadOnlyList<FolderIdentityMapping>> CreateCollectionsAsync(
        InitialSynchronizationSourceTree sourceTree,
        CancellationToken cancellationToken)
    {
        var mappings = new List<FolderIdentityMapping>(sourceTree.Folders.Count);
        var raindropIdByXbelId = new Dictionary<long, long>();
        var usedRaindropIds = new HashSet<long>();

        foreach (var sourceFolder in sourceTree.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? parentRaindropId = sourceFolder.ParentXbelId is { } parentXbelId
                ? raindropIdByXbelId[parentXbelId]
                : null;
            var created = await _raindropClient.CreateCollectionAsync(
                new RaindropCollectionWrite(sourceFolder.Folder.Title, parentRaindropId),
                cancellationToken);
            if (created.ParentId != parentRaindropId || !usedRaindropIds.Add(created.Id))
            {
                throw new InvalidOperationException(
                    "Raindrop returned an inconsistent collection during initial import.");
            }

            raindropIdByXbelId.Add(sourceFolder.Folder.Id, created.Id);
            mappings.Add(new FolderIdentityMapping(
                sourceFolder.StableId,
                sourceFolder.Folder.Id,
                created.Id));
        }

        return mappings.AsReadOnly();
    }

    private async Task<IReadOnlyList<BookmarkIdentityMapping>> CreateBookmarksAsync(
        InitialSynchronizationSourceTree sourceTree,
        IReadOnlyList<FolderIdentityMapping> folderMappings,
        CancellationToken cancellationToken)
    {
        var raindropFolderIds = folderMappings.ToDictionary(
            mapping => mapping.XbelId,
            mapping => mapping.RaindropCollectionId);
        var mappings = new List<BookmarkIdentityMapping>(sourceTree.Bookmarks.Count);
        var usedRaindropIds = new HashSet<long>();

        foreach (var sourceBookmark in sourceTree.Bookmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collectionId = sourceBookmark.ParentXbelId is { } parentXbelId
                ? raindropFolderIds[parentXbelId]
                : UnsortedCollectionId;
            var created = await CreateSingleBookmarkAsync(
                new RaindropBookmarkWrite(
                    collectionId,
                    sourceBookmark.Bookmark.Title,
                    sourceBookmark.Bookmark.Url),
                cancellationToken);
            if (created.CollectionId != collectionId || !usedRaindropIds.Add(created.Id))
            {
                throw new InvalidOperationException(
                    "Raindrop returned an inconsistent bookmark during initial import.");
            }

            mappings.Add(new BookmarkIdentityMapping(
                sourceBookmark.StableId,
                sourceBookmark.Bookmark.Id,
                created.Id));

            if (mappings.Count % 50 == 0 || mappings.Count == sourceTree.Bookmarks.Count)
            {
                _logger.Info($"Initial import Git -> Raindrop: created {mappings.Count}/"
                    + $"{sourceTree.Bookmarks.Count} bookmarks.");
            }
        }

        return mappings.AsReadOnly();
    }

    private async Task<RaindropBookmark> CreateSingleBookmarkAsync(
        RaindropBookmarkWrite bookmark,
        CancellationToken cancellationToken)
    {
        RaindropBookmark? created = null;
        var batches = 0;
        await foreach (var batch in _raindropClient.CreateBookmarksAsync([bookmark], cancellationToken))
        {
            batches++;
            if (batches != 1 || batch.Count != 1)
            {
                throw new InvalidOperationException(
                    "Raindrop returned an ambiguous result for a single bookmark creation.");
            }

            created = batch[0];
        }

        return batches == 1 && created is not null
            ? created
            : throw new InvalidOperationException(
                "Raindrop did not confirm a single bookmark creation.");
    }

    private async Task<BookmarkTree> ReadAndValidateImportedTreeAsync(
        InitialSynchronizationSourceTree sourceTree,
        IReadOnlyList<FolderIdentityMapping> folderMappings,
        IReadOnlyList<BookmarkIdentityMapping> bookmarkMappings,
        CancellationToken cancellationToken)
    {
        var collections = await _raindropClient.GetCollectionsAsync(cancellationToken);
        var bookmarks = await _raindropClient.GetActiveBookmarksAsync(cancellationToken);
        ValidateRaindropSnapshot(collections, bookmarks);

        var folderMappingByRaindropId = folderMappings.ToDictionary(
            mapping => mapping.RaindropCollectionId);
        var bookmarkMappingByRaindropId = bookmarkMappings.ToDictionary(
            mapping => mapping.RaindropBookmarkId);
        ValidateImportedMembership(collections, bookmarks, folderMappingByRaindropId, bookmarkMappingByRaindropId);

        var stableFolderIdByRaindropId = folderMappings.ToDictionary(
            mapping => mapping.RaindropCollectionId,
            mapping => mapping.StableId);
        var raindropFolders = CreateImportedFolders(sourceTree, collections, folderMappingByRaindropId, stableFolderIdByRaindropId);

        var raindropBookmarks = CreateImportedBookmarks(sourceTree, bookmarks, bookmarkMappingByRaindropId, stableFolderIdByRaindropId);

        return new BookmarkTree(raindropFolders, raindropBookmarks);
    }

    private static InitialSynchronizationSourceTree CreateSourceTree(XbelDocument document)
    {
        var folders = new List<InitialSynchronizationSourceFolder>();
        var bookmarks = new List<InitialSynchronizationSourceBookmark>();
        var stableFolderIdByXbelId = new Dictionary<long, StableFolderId>();
        var treeFolders = new List<BookmarkTreeFolder>();
        var treeBookmarks = new List<BookmarkTreeBookmark>();

        AddItems(document.Items, parentXbelId: null, parentStableId: null);

        return new InitialSynchronizationSourceTree(
            folders,
            bookmarks,
            folders.ToDictionary(folder => folder.Folder.Id),
            bookmarks.ToDictionary(bookmark => bookmark.Bookmark.Id),
            stableFolderIdByXbelId,
            new BookmarkTree(treeFolders, treeBookmarks));

        void AddItems(
            IReadOnlyList<XbelItem> items,
            long? parentXbelId,
            StableFolderId? parentStableId)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case XbelFolder folder:
                        {
                            var stableId = new StableFolderId(Guid.NewGuid());
                            stableFolderIdByXbelId.Add(folder.Id, stableId);
                            folders.Add(new InitialSynchronizationSourceFolder(folder, parentXbelId, stableId));
                            treeFolders.Add(new BookmarkTreeFolder(stableId, parentStableId, folder.Title));
                            AddItems(folder.Children, folder.Id, stableId);
                            break;
                        }
                    case XbelBookmark bookmark:
                        {
                            var stableId = new StableBookmarkId(Guid.NewGuid());
                            bookmarks.Add(new InitialSynchronizationSourceBookmark(bookmark, parentXbelId, stableId));
                            treeBookmarks.Add(new BookmarkTreeBookmark(
                                stableId,
                                parentStableId,
                                bookmark.Title,
                                bookmark.Url));
                            break;
                        }
                    default:
                        throw new XbelFormatException(
                            $"XBEL item {item.Id} has unsupported type '{item.GetType().Name}'.");
                }
            }
        }
    }

    private static void ValidateRaindropInputs(InitialSynchronizationSourceTree sourceTree)
    {
        foreach (var sourceBookmark in sourceTree.Bookmarks)
        {
            if (sourceBookmark.Bookmark.Title.Length > MaximumRaindropBookmarkTitleLength)
            {
                throw new XbelFormatException(
                    $"XBEL bookmark {sourceBookmark.Bookmark.Id} has a title exceeding the Raindrop limit "
                    + $"of {MaximumRaindropBookmarkTitleLength} characters.");
            }
        }
    }

    private static void ValidateRaindropSnapshot(
        IReadOnlyList<RaindropCollection> collections,
        IReadOnlyList<RaindropBookmark> bookmarks)
    {
        var collectionIds = new HashSet<long>();
        foreach (var collection in collections)
        {
            if (!collectionIds.Add(collection.Id))
            {
                throw new InvalidOperationException("Raindrop returned duplicate collection IDs.");
            }
        }

        _ = GetCollectionDepths(collections);

        var bookmarkIds = new HashSet<long>();
        foreach (var bookmark in bookmarks)
        {
            if (!bookmarkIds.Add(bookmark.Id)
                || bookmark.CollectionId != UnsortedCollectionId
                && !collectionIds.Contains(bookmark.CollectionId))
            {
                throw new InvalidOperationException(
                    "Raindrop returned duplicate bookmarks or a bookmark in an unknown collection.");
            }
        }
    }

    private static IReadOnlyDictionary<long, int> GetCollectionDepths(
        IReadOnlyList<RaindropCollection> collections)
    {
        var byId = collections.ToDictionary(collection => collection.Id);
        var depths = new Dictionary<long, int>();
        foreach (var collection in collections)
        {
            GetDepth(collection, new HashSet<long>());
        }

        return depths;

        int GetDepth(RaindropCollection collection, ISet<long> visited)
        {
            if (depths.TryGetValue(collection.Id, out var knownDepth))
            {
                return knownDepth;
            }

            if (!visited.Add(collection.Id))
            {
                throw new InvalidOperationException("Raindrop returned a cyclic collection tree.");
            }

            var depth = 0;
            if (collection.ParentId is { } parentId)
            {
                if (!byId.TryGetValue(parentId, out var parent))
                {
                    throw new InvalidOperationException(
                        "Raindrop returned a collection whose parent is missing.");
                }

                depth = checked(GetDepth(parent, visited) + 1);
            }

            visited.Remove(collection.Id);
            depths.Add(collection.Id, depth);
            return depth;
        }
    }

}
