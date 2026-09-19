using System.Text.Json;
using RaindropToFloccus.Clients;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.IntegrationTests;

public static class OfflineRaindropData
{
    public const string Token = "offline-canary-token-never-log";
    public const string Link = "https://example.com/bookmark";

    public static RaindropApiClient Client(IHttpClientFactory factory) =>
        new(factory, new IntegrationTestConfiguration(Token));

    public static RaindropBookmarkWrite[] Inputs(int count) => Enumerable.Range(0, count)
        .Select(i => new RaindropBookmarkWrite(-1, $"item-{i}", $"https://example.com/{i}")).ToArray();

    public static object Bookmark(long id, long collectionId = -1, string title = "bookmark", string link = Link) =>
        new { _id = id, collection = new Dictionary<string, long> { ["$id"] = collectionId }, title, link };

    public static string Items(IEnumerable<long> ids) =>
        JsonSerializer.Serialize(new { result = true, items = ids.Select(id => Bookmark(id)) });

    public static string Item(long id = 1, long collectionId = -1, string title = "bookmark", string link = Link) =>
        JsonSerializer.Serialize(new { result = true, item = Bookmark(id, collectionId, title, link) });

    public static IEnumerable<long> Ids(int start, int count) => Enumerable.Range(start, count).Select(i => (long)i);

    public static async Task<List<RaindropBookmark>> CreateAsync(RaindropApiClient client, int count)
    {
        var items = new List<RaindropBookmark>();
        await foreach (var batch in client.CreateBookmarksAsync(Inputs(count)))
        {
            items.AddRange(batch);
        }

        return items;
    }
}
