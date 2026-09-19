using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using ApplicationConfigurationProvider = RaindropToFloccus.Interfaces.IConfigurationProvider;

namespace RaindropToFloccus.Clients;

public sealed class RaindropApiClient : IRaindropClient
{
    public const string HttpClientName = "Raindrop";
    private const int PageSize = 50;
    private const int BatchSize = 100;
    private const long UnsortedCollectionId = -1;
    private const long TrashCollectionId = -99;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private static readonly Uri ApiRoot = new("https://api.raindrop.io/rest/v1/");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ApplicationConfigurationProvider _configurationProvider;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _nextRequestAt;

    public RaindropApiClient(IHttpClientFactory httpClientFactory,
        ApplicationConfigurationProvider configurationProvider, TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _configurationProvider = configurationProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RaindropCollection>> GetCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var collections = new List<RaindropCollection>();
        var byId = new Dictionary<long, RaindropCollection>();
        foreach (var path in new[] { "collections", "collections/childrens" })
        {
            var items = await SendAsync(HttpMethod.Get, path, null, "read collections", root =>
                ReadItems(root).Select(ReadCollection).ToArray(), cancellationToken);
            MergeCollections(items, byId, collections);
        }

        return collections.AsReadOnly();
    }

    public async Task<IReadOnlyList<RaindropBookmark>> GetActiveBookmarksAsync(
        CancellationToken cancellationToken = default)
    {
        var bookmarks = new List<RaindropBookmark>();
        var seenIds = new HashSet<long>();
        // Collection 0 includes Unsorted and all user collections, but excludes Trash.
        for (var page = 0; ; page = checked(page + 1))
        {
            var path = FormattableString.Invariant($"raindrops/0?perpage={PageSize}&page={page}&sort=created");
            var items = await SendAsync(HttpMethod.Get, path, null, "read bookmarks", root =>
                ReadItems(root).Select(ReadBookmark).ToArray(), cancellationToken);
            if (items.Length > PageSize)
            {
                throw InvalidResponse("read bookmarks", false);
            }

            AppendActiveBookmarkPage(items, seenIds, bookmarks);

            if (items.Length < PageSize)
            {
                return bookmarks.AsReadOnly();
            }
        }
    }

    private static void MergeCollections(IEnumerable<RaindropCollection> items,
        Dictionary<long, RaindropCollection> byId, List<RaindropCollection> collections)
    {
        foreach (var item in items)
        {
            if (byId.TryGetValue(item.Id, out var previous))
            {
                // The two endpoints overlap. Repeated representations of the same ID
                // are safe only when all synchronized fields agree; conflicting versions
                // indicate a changing snapshot and must be reconciled on a later read.
                if (previous != item)
                {
                    throw InvalidResponse("read collections", false);
                }

                continue;
            }

            byId.Add(item.Id, item);
            collections.Add(item);
        }
    }

    private static void AppendActiveBookmarkPage(IEnumerable<RaindropBookmark> items,
        HashSet<long> seenIds, List<RaindropBookmark> bookmarks)
    {
        foreach (var item in items)
        {
            // Repeated IDs indicate a moving/repeated page, not duplicate URLs. Reject the
            // incomplete snapshot instead of interpreting missing records as deletions.
            if (!seenIds.Add(item.Id))
            {
                throw InvalidResponse("read bookmarks", false);
            }

            if (item.CollectionId != TrashCollectionId)
            {
                bookmarks.Add(item);
            }
        }
    }

    private static RaindropBookmark[] ReadCreatedBookmarkBatch(JsonElement root, int expectedCount,
        HashSet<long> seenIds)
    {
        var items = ReadItems(root).Select(ReadBookmark).ToArray();
        RequireResponse(items.Length == expectedCount);
        foreach (var item in items)
        {
            RequireResponse(item.CollectionId != TrashCollectionId && seenIds.Add(item.Id));
        }

        return items;
    }

    public Task<RaindropCollection> CreateCollectionAsync(
        RaindropCollectionWrite collection, CancellationToken cancellationToken = default)
    {
        ValidateCollection(collection);
        return SendAsync(HttpMethod.Post, "collection", CollectionBody(collection), "create collection",
            root => ReadCollection(ReadProperty(root, "item")), cancellationToken);
    }

    public Task<RaindropCollection> UpdateCollectionAsync(
        long id, RaindropCollectionWrite collection, CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(id);
        ValidateCollection(collection);
        if (id == collection.ParentId)
        {
            throw new ArgumentException("A collection cannot be its own parent.", nameof(collection));
        }

        return SendAsync(HttpMethod.Put, $"collection/{IdText(id)}", CollectionBody(collection),
            "update collection", root => ReadUpdatedCollection(root, id), cancellationToken);
    }

    public async Task DeleteCollectionsAsync(IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        var validatedIds = ValidateIds(ids);
        foreach (var batch in validatedIds.Chunk(BatchSize))
        {
            await SendAsync(HttpMethod.Delete, "collections", new { ids = batch }, "delete collections",
                _ => true, cancellationToken, allowNoContent: true);
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<RaindropBookmark>> CreateBookmarksAsync(
        IReadOnlyList<RaindropBookmarkWrite> bookmarks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        var validatedBookmarks = bookmarks.ToArray();
        // Validate the entire input before the first side effect, without URL/title deduplication.
        foreach (var bookmark in validatedBookmarks)
        {
            ValidateBookmark(bookmark);
        }

        var seenIds = new HashSet<long>();
        foreach (var batch in validatedBookmarks.Chunk(BatchSize))
        {
            var created = await SendAsync(HttpMethod.Post, "raindrops",
                new { items = batch.Select(BookmarkBody).ToArray() }, "create bookmarks", root =>
                    ReadCreatedBookmarkBatch(root, batch.Length, seenIds), cancellationToken);
            yield return Array.AsReadOnly(created);
        }
    }

    public Task<RaindropBookmark> UpdateBookmarkAsync(long id, RaindropBookmarkWrite bookmark,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(id);
        ValidateBookmark(bookmark);
        return SendAsync(HttpMethod.Put, $"raindrop/{IdText(id)}", BookmarkBody(bookmark), "update bookmark",
            root => ReadUpdatedBookmark(root, id), cancellationToken);
    }

    public async Task MoveBookmarksAsync(long sourceCollectionId, long targetCollectionId,
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        ValidateActiveCollectionId(sourceCollectionId);
        ValidateActiveCollectionId(targetCollectionId);
        var validatedIds = ValidateIds(ids);
        foreach (var batch in validatedIds.Chunk(BatchSize))
        {
            await SendAsync(HttpMethod.Put, $"raindrops/{IdText(sourceCollectionId)}",
                new { ids = batch, collection = Reference(targetCollectionId) }, "move bookmarks",
                root => ValidateMoveResponse(root, batch.Length), cancellationToken, allowNoContent: true);
        }
    }

    public async Task TrashBookmarksAsync(long sourceCollectionId, IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ValidateActiveCollectionId(sourceCollectionId);
        var validatedIds = ValidateIds(ids);
        foreach (var batch in validatedIds.Chunk(BatchSize))
        {
            // Never DELETE /raindrop/{id}: repeating it after an uncertain result can permanently
            // delete an item already in Trash. Scope deletions to the original active collection.
            await SendAsync(HttpMethod.Delete, $"raindrops/{IdText(sourceCollectionId)}",
                new { ids = batch }, "trash bookmarks", _ => true, cancellationToken, allowNoContent: true);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string operation,
        Func<JsonElement, T> readResponse, CancellationToken cancellationToken, bool allowNoContent = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isWrite = method != HttpMethod.Get;
        try
        {
            await _rateLimitGate.WaitAsync(cancellationToken);
            try
            {
                while (true)
                {
                    // A 429 response proves that the request was rejected before the write was applied,
                    // so retrying that exact request after the advertised reset is safe. Other write
                    // failures are deliberately not retried because their outcome can be unknown.
                    await WaitForRateLimitAsync(cancellationToken);
                    using var client = _httpClientFactory.CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(method, new Uri(ApiRoot, path));
                    ConfigureRequest(request, body);

                    // Stop before the next request, but let a started write reach its response boundary.
                    // The configured HttpClient timeout still bounds the request.
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestCancellation = isWrite ? CancellationToken.None : cancellationToken;
                    using var response = await client.SendAsync(request, requestCancellation);
                    DeferWhenRateLimited(response);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests && isWrite)
                    {
                        continue;
                    }

                    return await ReadResponseAsync(response, operation, isWrite, readResponse, allowNoContent,
                        requestCancellation);
                }
            }
            finally
            {
                _rateLimitGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new RaindropApiException(operation, "The request timed out.", isTransient: true,
                outcomeMayBeUnknown: isWrite);
        }
        catch (HttpRequestException)
        {
            throw new RaindropApiException(operation, "A network or HTTP transport error occurred.", isTransient: true,
                outcomeMayBeUnknown: isWrite);
        }
        catch (IOException)
        {
            throw new RaindropApiException(operation, "The response could not be read.", isTransient: true,
                outcomeMayBeUnknown: isWrite);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw InvalidResponse(operation, isWrite);
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        while (_nextRequestAt > _timeProvider.GetUtcNow())
        {
            await Task.Delay(_nextRequestAt - _timeProvider.GetUtcNow(), _timeProvider, cancellationToken);
        }
    }

    private void DeferWhenRateLimited(HttpResponseMessage response)
    {
        var hasNoRequestsRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining)
            && remaining == 0;
        if (response.StatusCode != HttpStatusCode.TooManyRequests && !hasNoRequestsRemaining)
        {
            return;
        }

        // Raindrop publishes a one-minute per-user window. A server-supplied reset is more precise;
        // the window is a conservative fallback when an intermediary omits that header.
        var delay = ReadRetryAfter(response) ?? RateLimitWindow;
        var notBefore = _timeProvider.GetUtcNow() + delay;
        if (notBefore > _nextRequestAt)
        {
            _nextRequestAt = notBefore;
        }
    }

    private void ConfigureRequest(HttpRequestMessage request, object? body)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configurationProvider.RaindropApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, string operation,
        bool isWrite, Func<JsonElement, T> readResponse, bool allowNoContent, CancellationToken requestCancellation)
    {
        if (!response.IsSuccessStatusCode)
        {
            var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            throw new RaindropApiException(operation, $"HTTP {(int)response.StatusCode}.", response.StatusCode,
                transient, ReadRetryAfter(response), isWrite && transient);
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            if (!allowNoContent)
            {
                throw InvalidResponse(operation, isWrite);
            }

            return readResponse(default);
        }

        var content = await response.Content.ReadAsByteArrayAsync(requestCancellation);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var result = ReadProperty(root, "result");
        if (result.ValueKind == JsonValueKind.False)
        {
            // Deliberately exclude raw errorMessage, body, headers and underlying exceptions:
            // an upstream error can echo user data or credentials.
            throw new RaindropApiException(operation, "The API reported result=false.", response.StatusCode,
                outcomeMayBeUnknown: isWrite);
        }

        RequireResponse(result.ValueKind == JsonValueKind.True);
        return readResponse(root);
    }

    private static RaindropCollection ReadCollection(JsonElement item)
    {
        var id = ReadId(item, "_id");
        RequireResponse(id > 0);
        long? parentId = null;
        if (item.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
        {
            parentId = ReadId(parent, "$id");
            RequireResponse(parentId > 0 && parentId != id);
        }

        return new RaindropCollection(id, parentId, ReadText(item, "title"));
    }

    private static RaindropCollection ReadUpdatedCollection(JsonElement root, long expectedId)
    {
        var result = ReadCollection(ReadProperty(root, "item"));
        RequireResponse(result.Id == expectedId);
        return result;
    }

    private static RaindropBookmark ReadBookmark(JsonElement item)
    {
        var id = ReadId(item, "_id");
        var collectionId = ReadId(ReadProperty(item, "collection"), "$id");
        var link = ReadText(item, "link");
        RequireResponse(id > 0 && (collectionId > 0 || collectionId is UnsortedCollectionId or TrashCollectionId)
            && !string.IsNullOrWhiteSpace(link));
        return new RaindropBookmark(id, collectionId, ReadText(item, "title"), link);
    }

    private static RaindropBookmark ReadUpdatedBookmark(JsonElement root, long expectedId)
    {
        var result = ReadBookmark(ReadProperty(root, "item"));
        RequireResponse(result.Id == expectedId && result.CollectionId != TrashCollectionId);
        return result;
    }

    private static bool ValidateMoveResponse(JsonElement root, int expectedCount)
    {
        // A JSON response carries an explicit modification count. Fewer modified records than
        // requested means the move outcome is incomplete and must be reconciled by rereading.
        if (root.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        var modified = ReadProperty(root, "modified");
        RequireResponse(
            modified.ValueKind == JsonValueKind.Number
            && modified.TryGetInt32(out var count)
            && count == expectedCount);
        return true;
    }

    private static JsonElement.ArrayEnumerator ReadItems(JsonElement root)
    {
        var items = ReadProperty(root, "items");
        RequireResponse(items.ValueKind == JsonValueKind.Array);
        return items.EnumerateArray();
    }

    private static JsonElement ReadProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            throw new InvalidDataException();
        }

        return property;
    }

    private static long ReadId(JsonElement element, string name)
    {
        var property = ReadProperty(element, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var id))
        {
            throw new InvalidDataException();
        }

        return id;
    }

    private static string ReadText(JsonElement element, string name)
    {
        var property = ReadProperty(element, name);
        RequireResponse(property.ValueKind == JsonValueKind.String);
        return property.GetString()!;
    }

    private static void RequireResponse(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException();
        }
    }

    private static RaindropApiException InvalidResponse(string operation, bool isWrite) =>
        new(operation, "The API response is incomplete, inconsistent or malformed.", isTransient: true,
            outcomeMayBeUnknown: isWrite);

    private static object BookmarkBody(RaindropBookmarkWrite bookmark) =>
        new { title = bookmark.Title, link = bookmark.Link, collection = Reference(bookmark.CollectionId) };

    private static object CollectionBody(RaindropCollectionWrite collection) =>
        new { title = collection.Title, parent = collection.ParentId is { } id ? Reference(id) : null };

    private static Dictionary<string, long> Reference(long id) => new() { ["$id"] = id };

    private static string IdText(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static void ValidateCollection(RaindropCollectionWrite collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(collection.Title);
        if (collection.ParentId is { } parentId)
        {
            ValidatePositiveId(parentId);
        }
    }

    private static void ValidateBookmark(RaindropBookmarkWrite bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        ArgumentNullException.ThrowIfNull(bookmark.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmark.Link);
        if (bookmark.Title.Length > 1000)
        {
            throw new ArgumentException("A bookmark title must not exceed 1000 characters.", nameof(bookmark));
        }

        ValidateActiveCollectionId(bookmark.CollectionId);
    }

    private static long[] ValidateIds(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var snapshot = ids.ToArray();
        var seenIds = new HashSet<long>();
        foreach (var id in snapshot)
        {
            ValidatePositiveId(id);
            if (!seenIds.Add(id))
            {
                throw new ArgumentException("IDs must be unique within an operation.", nameof(ids));
            }
        }

        return snapshot;
    }

    private static void ValidatePositiveId(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
    }

    private static void ValidateActiveCollectionId(long id)
    {
        if (id != UnsortedCollectionId && id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                "Only positive user collection IDs and -1 (Unsorted) are allowed.");
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        var now = DateTimeOffset.UtcNow;
        if (retryAfter?.Date is { } date)
        {
            return date > now ? date - now : TimeSpan.Zero;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= -62_135_596_800 and <= 253_402_300_799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return reset > now ? reset - now : TimeSpan.Zero;
        }

        return null;
    }
}
