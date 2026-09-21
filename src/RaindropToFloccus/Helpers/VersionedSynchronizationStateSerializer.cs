namespace RaindropToFloccus.Helpers;

public sealed class VersionedSynchronizationStateSerializer : ISynchronizationStateSerializer
{
    private const int SchemaVersion = 1;

    private const int MaximumDocumentCharacters = 16 * 1024 * 1024;
    private const int MaximumFolderDepth = 128;
    private const long MaximumFloccusId = 9_007_199_254_740_991;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public int CurrentSchemaVersion => SchemaVersion;

    public SynchronizationState Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new SynchronizationStateFormatException("The synchronization state is empty.");
        }

        if (content.Length > MaximumDocumentCharacters)
        {
            throw new SynchronizationStateFormatException("The synchronization state exceeds the size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumFolderDepth + 8
                });

            EnsureNoDuplicateProperties(document.RootElement, "$", depth: 0);
            ValidateDeclaredVersion(document.RootElement);

            var state = JsonSerializer.Deserialize<SynchronizationState>(content, SerializerOptions)
                ?? throw new SynchronizationStateFormatException(
                    "The synchronization state does not contain an object.");
            Validate(state);
            return state;
        }
        catch (SynchronizationStateFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state is not valid versioned JSON.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state contains an unsupported value.",
                exception);
        }
    }

    public string Serialize(SynchronizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        var canonicalState = Canonicalize(state);
        return JsonSerializer.Serialize(canonicalState, SerializerOptions) + "\n";
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            MaxDepth = MaximumFolderDepth + 8,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new StableFolderIdJsonConverter());
        options.Converters.Add(new StableBookmarkIdJsonConverter());
        return options;
    }

    private static void ValidateDeclaredVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state root must be a JSON object.");
        }

        if (!root.TryGetProperty("schemaVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state must declare an integer schemaVersion.");
        }

        if (version != SchemaVersion)
        {
            throw new SynchronizationStateFormatException(
                $"Synchronization state schema version {version} is not supported; expected {SchemaVersion}.");
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path, int depth)
    {
        if (depth > MaximumFolderDepth + 8)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state exceeds the nesting limit.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new SynchronizationStateFormatException(
                        $"The synchronization state contains duplicate property '{property.Name}' at {path}.");
                }

                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    public void Validate(SynchronizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != SchemaVersion)
        {
            throw new SynchronizationStateFormatException(
                $"Synchronization state schema version {state.SchemaVersion} is not supported; expected {SchemaVersion}.");
        }

        if (state.Generation <= 0)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state generation must be positive.");
        }

        if (!state.InitializationCompleted)
        {
            throw new SynchronizationStateFormatException(
                "An existing synchronization state must represent a completed initialization.");
        }

        if (state.XbelSnapshot is null || state.RaindropSnapshot is null)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state must contain both endpoint snapshots.");
        }

        var xbelFolders = ValidateTree(state.XbelSnapshot, "xbelSnapshot");
        var raindropFolders = ValidateTree(state.RaindropSnapshot, "raindropSnapshot");
        ValidateEquivalentIdentitySets(state.XbelSnapshot, state.RaindropSnapshot);
        ValidateFolderStructure(xbelFolders, raindropFolders);
        ValidateBookmarkStructure(state.XbelSnapshot, state.RaindropSnapshot);
        ValidateMappings(state, xbelFolders.Keys, state.XbelSnapshot.Bookmarks.Select(item => item.Id));
    }

    private static IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> ValidateFolders(
        IReadOnlyList<BookmarkTreeFolder> sourceFolders, string name)
    {
        var folders = new Dictionary<StableFolderId, BookmarkTreeFolder>();
        foreach (var folder in sourceFolders)
        {
            if (folder is null)
            {
                throw new SynchronizationStateFormatException($"The {name} tree contains a null folder.");
            }

            ValidateStableId(folder.Id.Value, name, "folder");
            if (!folders.TryAdd(folder.Id, folder))
            {
                throw new SynchronizationStateFormatException(
                    $"The {name} tree contains duplicate stable folder ID {folder.Id.Value:D}.");
            }

            if (folder.Title is null)
            {
                throw new SynchronizationStateFormatException(
                    $"Folder {folder.Id.Value:D} in {name} has no title.");
            }
        }
        return folders;
    }

    private static void ValidateBookmarks(IReadOnlyList<BookmarkTreeBookmark> bookmarks,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> folders, string name)
    {
        var bookmarkIds = new HashSet<StableBookmarkId>();
        foreach (var bookmark in bookmarks)
        {
            if (bookmark is null)
            {
                throw new SynchronizationStateFormatException($"The {name} tree contains a null bookmark.");
            }

            ValidateStableId(bookmark.Id.Value, name, "bookmark");
            if (!bookmarkIds.Add(bookmark.Id))
            {
                throw new SynchronizationStateFormatException(
                    $"The {name} tree contains duplicate stable bookmark ID {bookmark.Id.Value:D}.");
            }

            if (bookmark.ParentId is { } parentId && !folders.ContainsKey(parentId))
            {
                throw new SynchronizationStateFormatException(
                    $"Bookmark {bookmark.Id.Value:D} in {name} refers to a missing parent.");
            }

            if (bookmark.Title is null || string.IsNullOrWhiteSpace(bookmark.Url))
            {
                throw new SynchronizationStateFormatException(
                    $"Bookmark {bookmark.Id.Value:D} in {name} has an invalid title or URL.");
            }
        }
    }

    private static void ValidateFolderMappings(IReadOnlyList<FolderIdentityMapping> mappings,
        IEnumerable<StableFolderId> folderIds, HashSet<long> xbelIds)
    {
        var expectedFolderIds = folderIds.ToHashSet();
        var mappedFolderIds = new HashSet<StableFolderId>();
        var raindropCollectionIds = new HashSet<long>();
        foreach (var mapping in mappings)
        {
            if (mapping is null)
            {
                throw new SynchronizationStateFormatException("The folder mappings contain a null entry.");
            }

            ValidateStableId(mapping.StableId.Value, "folderMappings", "mapping");
            if (!mappedFolderIds.Add(mapping.StableId)
                || !xbelIds.Add(mapping.XbelId)
                || !raindropCollectionIds.Add(mapping.RaindropCollectionId))
            {
                throw new SynchronizationStateFormatException(
                    "Folder mappings contain a duplicate stable or source-specific ID.");
            }

            ValidateSourceId(mapping.XbelId, "XBEL folder", MaximumFloccusId);
            ValidateSourceId(mapping.RaindropCollectionId, "Raindrop collection", long.MaxValue);
        }

        if (!mappedFolderIds.SetEquals(expectedFolderIds))
        {
            throw new SynchronizationStateFormatException(
                "Folder mappings must cover every snapshot folder exactly once.");
        }
    }

    private static void ValidateBookmarkMappings(IReadOnlyList<BookmarkIdentityMapping> mappings,
        IEnumerable<StableBookmarkId> bookmarkIds, HashSet<long> xbelIds)
    {
        var expectedBookmarkIds = bookmarkIds.ToHashSet();
        var mappedBookmarkIds = new HashSet<StableBookmarkId>();
        var raindropBookmarkIds = new HashSet<long>();
        foreach (var mapping in mappings)
        {
            if (mapping is null)
            {
                throw new SynchronizationStateFormatException("The bookmark mappings contain a null entry.");
            }

            ValidateStableId(mapping.StableId.Value, "bookmarkMappings", "mapping");
            if (!mappedBookmarkIds.Add(mapping.StableId)
                || !xbelIds.Add(mapping.XbelId)
                || !raindropBookmarkIds.Add(mapping.RaindropBookmarkId))
            {
                throw new SynchronizationStateFormatException(
                    "Bookmark mappings contain a duplicate stable or source-specific ID.");
            }

            ValidateSourceId(mapping.XbelId, "XBEL bookmark", MaximumFloccusId);
            ValidateSourceId(mapping.RaindropBookmarkId, "Raindrop bookmark", long.MaxValue);
        }

        if (!mappedBookmarkIds.SetEquals(expectedBookmarkIds))
        {
            throw new SynchronizationStateFormatException(
                "Bookmark mappings must cover every snapshot bookmark exactly once.");
        }
    }

    private static IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> ValidateTree(
        BookmarkTree tree,
        string name)
    {
        if (tree.Folders is null || tree.Bookmarks is null)
        {
            throw new SynchronizationStateFormatException(
                $"The {name} tree contains a missing item collection.");
        }

        var folders = ValidateFolders(tree.Folders, name);
        foreach (var folder in folders.Values)
        {
            if (folder.ParentId is { } parentId && !folders.ContainsKey(parentId))
            {
                throw new SynchronizationStateFormatException(
                    $"Folder {folder.Id.Value:D} in {name} refers to a missing parent.");
            }

            ValidateFolderAncestry(folder, folders, name);
        }

        ValidateBookmarks(tree.Bookmarks, folders, name);

        return folders;
    }

    private static void ValidateStableId(Guid value, string treeName, string entityName)
    {
        if (value == Guid.Empty)
        {
            throw new SynchronizationStateFormatException(
                $"The {treeName} tree contains a {entityName} with an empty stable ID.");
        }
    }

    private static void ValidateFolderAncestry(
        BookmarkTreeFolder folder,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> folders,
        string treeName)
    {
        var visited = new HashSet<StableFolderId> { folder.Id };
        var current = folder;
        var depth = 0;
        while (current.ParentId is { } parentId)
        {
            if (!visited.Add(parentId))
            {
                throw new SynchronizationStateFormatException(
                    $"The {treeName} tree contains a folder cycle involving {folder.Id.Value:D}.");
            }

            depth++;
            if (depth >= MaximumFolderDepth)
            {
                throw new SynchronizationStateFormatException(
                    $"The {treeName} tree exceeds the supported folder depth of {MaximumFolderDepth}.");
            }

            current = folders[parentId];
        }
    }

    private static void ValidateEquivalentIdentitySets(BookmarkTree xbel, BookmarkTree raindrop)
    {
        if (!xbel.Folders.Select(item => item.Id).ToHashSet()
                .SetEquals(raindrop.Folders.Select(item => item.Id))
            || !xbel.Bookmarks.Select(item => item.Id).ToHashSet()
                .SetEquals(raindrop.Bookmarks.Select(item => item.Id)))
        {
            throw new SynchronizationStateFormatException(
                "The endpoint snapshots must contain the same stable entity identities.");
        }
    }

    private static void ValidateFolderStructure(
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> xbelFolders,
        IReadOnlyDictionary<StableFolderId, BookmarkTreeFolder> raindropFolders)
    {
        foreach (var (id, xbelFolder) in xbelFolders)
        {
            if (xbelFolder.ParentId != raindropFolders[id].ParentId)
            {
                throw new SynchronizationStateFormatException(
                    $"Folder {id.Value:D} has different parents in the reconciled snapshots.");
            }
        }
    }

    private static void ValidateBookmarkStructure(BookmarkTree xbel, BookmarkTree raindrop)
    {
        var raindropBookmarks = raindrop.Bookmarks.ToDictionary(item => item.Id);
        foreach (var bookmark in xbel.Bookmarks)
        {
            if (bookmark.ParentId != raindropBookmarks[bookmark.Id].ParentId)
            {
                throw new SynchronizationStateFormatException(
                    $"Bookmark {bookmark.Id.Value:D} has different parents in the reconciled snapshots.");
            }
        }
    }

    private static void ValidateMappings(
        SynchronizationState state,
        IEnumerable<StableFolderId> folderIds,
        IEnumerable<StableBookmarkId> bookmarkIds)
    {
        if (state.FolderMappings is null || state.BookmarkMappings is null)
        {
            throw new SynchronizationStateFormatException(
                "The synchronization state contains a missing mapping collection.");
        }

        var xbelIds = new HashSet<long>();
        ValidateFolderMappings(state.FolderMappings, folderIds, xbelIds);
        ValidateBookmarkMappings(state.BookmarkMappings, bookmarkIds, xbelIds);
    }

    private static void ValidateSourceId(long value, string name, long maximum)
    {
        if (value <= 0 || value > maximum)
        {
            throw new SynchronizationStateFormatException(
                $"A {name} mapping contains an ID outside the supported range.");
        }
    }

    private static SynchronizationState Canonicalize(SynchronizationState state)
    {
        return state with
        {
            XbelSnapshot = Canonicalize(state.XbelSnapshot),
            RaindropSnapshot = Canonicalize(state.RaindropSnapshot),
            FolderMappings = state.FolderMappings.OrderBy(item => item.StableId.Value).ToArray(),
            BookmarkMappings = state.BookmarkMappings.OrderBy(item => item.StableId.Value).ToArray()
        };
    }

    private static BookmarkTree Canonicalize(BookmarkTree tree)
    {
        return tree with
        {
            Folders = tree.Folders.OrderBy(item => item.Id.Value).ToArray(),
            Bookmarks = tree.Bookmarks.OrderBy(item => item.Id.Value).ToArray()
        };
    }
}
