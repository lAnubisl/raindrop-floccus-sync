using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RaindropToFloccus.Clients;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.IntegrationTests;

public sealed class LiveAccountFixture : IAsyncLifetime
{
    private readonly HashSet<string> _collectionTitles = [];
    private Dictionary<long, string> _originalCollections = [];
    private Dictionary<long, string> _originalBookmarks = [];
    private bool _initialized;
    private readonly HashSet<long> _overlappingCollectionIds = [];
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string Prefix => $"r2f-it-{RunId}-";
    public string LinkPrefix => $"https://example.com/raindrop-integration/{RunId}/";
    public LiveHttpObserver Observer { get; } = new();
    public IntegrationTestConfiguration Configuration { get; private set; } = null!;
    public IRaindropClient Client { get; private set; } = null!;
    public IHttpClientFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!IntegrationTestConfiguration.IsEnabled)
        {
            return;
        }

        Configuration = new();
        Factory = new LiveHttpClientFactory(Observer);
        Client = new RaindropApiClient(Factory, Configuration);
        _originalCollections = (await ReadCollectionsRawAsync()).ToDictionary(Id, CollectionSnapshot);
        _originalBookmarks = (await ReadActiveRawAsync()).ToDictionary(Id, BookmarkSnapshot);
        _initialized = true;
    }

    public string CollectionTitle(string label)
    {
        var title = Prefix + label;
        // Register before sending, so cleanup can recover writes with an unknown outcome.
        _collectionTitles.Add(title);
        return title;
    }

    public Task<RaindropCollection> CreateCollectionAsync(string label, long? parent = null) =>
        Client.CreateCollectionAsync(new(CollectionTitle(label), parent));

    public RaindropBookmarkWrite Bookmark(long collectionId, string label) =>
        new(collectionId, Prefix + label, LinkPrefix + label);

    public async Task<List<RaindropBookmark>> CreateBookmarksAsync(params RaindropBookmarkWrite[] inputs)
    {
        var result = new List<RaindropBookmark>();
        await foreach (var batch in Client.CreateBookmarksAsync(inputs))
        {
            result.AddRange(batch);
        }

        return result;
    }

    // Independent API reads avoid verifying the client's parser against itself.
    public async Task<JsonElement> ReadBookmarkRawAsync(long id) =>
        (await RawAsync(HttpMethod.Get, $"raindrop/{id}")).GetProperty("item");

    public async Task<JsonElement> RawAsync(HttpMethod method, string relativePath, object? body = null)
    {
        using var http = Factory.CreateClient(RaindropApiClient.HttpClientName);
        using var request = new HttpRequestMessage(method, "https://api.raindrop.io/rest/v1/" + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration.RaindropApiToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Independent API check failed: {method} {relativePath}, HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException($"Independent API check returned no success: {method} {relativePath}.");
        }

        return document.RootElement.Clone();
    }

    public async Task<List<JsonElement>> ReadCollectionsRawAsync()
    {
        var result = new List<JsonElement>();
        foreach (var path in new[] { "collections", "collections/childrens" })
        {
            result.AddRange((await RawAsync(HttpMethod.Get, path)).GetProperty("items").EnumerateArray());
        }

        var unique = new Dictionary<long, JsonElement>();
        foreach (var item in result)
        {
            var id = Id(item);
            if (unique.TryGetValue(id, out var previous))
            {
                // The live API can include nested collections in both endpoints. The independent
                // oracle merges identical IDs, but still rejects conflicting representations.
                if (CollectionSnapshot(previous) != CollectionSnapshot(item))
                {
                    throw new InvalidOperationException("A collection changed between independent API reads.");
                }

                _overlappingCollectionIds.Add(id);
            }

            unique[id] = item;
        }

        return unique.Values.ToList();
    }

    public async Task<List<JsonElement>> ReadActiveRawAsync()
    {
        var result = new List<JsonElement>();
        var seen = new HashSet<long>();
        for (var page = 0; page < 1000; page++)
        {
            var items = (await RawAsync(HttpMethod.Get, $"raindrops/0?perpage=50&page={page}&sort=created"))
                .GetProperty("items").EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (!seen.Add(Id(item)))
                {
                    throw new InvalidOperationException("Account changed during independent pagination; stop other writers and rerun.");
                }

                result.Add(item);
            }

            if (items.Length < 50)
            {
                return result;
            }
        }

        throw new InvalidOperationException("Independent pagination exceeded its safety limit.");
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (!_initialized)
            {
                return;
            }

            var active = await ReadActiveRawAsync();
            var ownBookmarks = active.Where(IsOwnBookmark).ToArray();
            foreach (var group in ownBookmarks.GroupBy(item => item.GetProperty("collection").GetProperty("$id").GetInt64()))
            {
                if (group.Key != -1 && group.Key <= 0)
                {
                    throw new InvalidOperationException("Cleanup refuses a non-active collection.");
                }

                foreach (var batch in group.Select(Id).Chunk(100))
                {
                    // Independent cleanup still works if a client response contract test fails.
                    await RawAsync(HttpMethod.Delete, $"raindrops/{group.Key}", new { ids = batch });
                }
            }

            var ownCollections = (await ReadCollectionsRawAsync())
                .Where(item => _collectionTitles.Contains(item.GetProperty("title").GetString()!))
                .Select(Id).ToArray();
            foreach (var batch in ownCollections.Chunk(100))
            {
                if (batch.Any(id => id <= 0 || _originalCollections.ContainsKey(id)))
                {
                    throw new InvalidOperationException("Cleanup refuses to remove an original or system collection.");
                }

                await RawAsync(HttpMethod.Delete, "collections", new { ids = batch });
            }

            var remainingCollections = await ReadCollectionsRawAsync();
            var remainingBookmarks = await ReadActiveRawAsync();
            Assert.DoesNotContain(remainingCollections, item => _collectionTitles.Contains(item.GetProperty("title").GetString()!));
            Assert.DoesNotContain(remainingBookmarks, IsOwnBookmark);
            var currentCollections = remainingCollections.ToDictionary(Id, CollectionSnapshot);
            var currentBookmarks = remainingBookmarks.ToDictionary(Id, BookmarkSnapshot);
            Assert.All(_originalCollections, pair => Assert.True(currentCollections.TryGetValue(pair.Key, out var value) && value == pair.Value,
                "An original collection changed during the run."));
            Assert.All(_originalBookmarks, pair => Assert.True(currentBookmarks.TryGetValue(pair.Key, out var value) && value == pair.Value,
                "An original bookmark changed during the run."));

            WriteReport(new
            {
                RunId,
                Prefix,
                CleanupSucceeded = true,
                OriginalCollectionsPreserved = _originalCollections.Count,
                OriginalBookmarksPreserved = _originalBookmarks.Count,
                OverlappingCollectionIds = _overlappingCollectionIds.Order().ToArray(),
                ActiveTestBookmarksRemaining = 0,
                TestCollectionsRemaining = 0,
                Note = "Test bookmarks were moved to Trash, not permanently deleted. Existing Trash was not emptied.",
                Requests = Observer.Requests
            });
        }
        catch
        {
            WriteReport(new { RunId, Prefix, CleanupSucceeded = false, Requests = Observer.Requests });
            throw;
        }
        finally
        {
            Observer.Dispose();
        }
    }

    private void WriteReport(object report)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"cleanup-{RunId}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool IsOwnBookmark(JsonElement item) =>
        item.GetProperty("link").GetString()!.StartsWith(LinkPrefix, StringComparison.Ordinal)
        && !_originalBookmarks.ContainsKey(Id(item));

    private static long Id(JsonElement item) => item.GetProperty("_id").GetInt64();

    private static string CollectionSnapshot(JsonElement item) => JsonSerializer.Serialize(new
    {
        Title = item.GetProperty("title").GetString(),
        Parent = item.TryGetProperty("parent", out var parent) ? parent.GetRawText() : "null"
    });

    private static string BookmarkSnapshot(JsonElement item) => JsonSerializer.Serialize(new
    {
        Title = item.GetProperty("title").GetString(),
        Link = item.GetProperty("link").GetString(),
        Collection = item.GetProperty("collection").GetProperty("$id").GetInt64()
    });
}
