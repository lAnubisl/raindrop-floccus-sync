using System.Net;
using System.Text.Json;
using RaindropToFloccus.Clients;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.IntegrationTests;

[Trait("Category", "RaindropIntegration")]
public sealed class RaindropClientTests(LiveAccountFixture account) : IClassFixture<LiveAccountFixture>
{
    [IntegrationFact]
    public async Task Authenticated_reads_match_independent_API_snapshots()
    {
        var root = await account.CreateCollectionAsync("read-root");
        await account.CreateCollectionAsync("read-child", root.Id);
        var collections = await account.Client.GetCollectionsAsync();
        var rawCollections = await account.ReadCollectionsRawAsync();
        Assert.Equal(rawCollections.Select(Id).Order(), collections.Select(item => item.Id).Order());
        var bookmarks = await account.Client.GetActiveBookmarksAsync();
        var rawBookmarks = await account.ReadActiveRawAsync();
        Assert.Equal(rawBookmarks.Select(Id).Order(), bookmarks.Select(item => item.Id).Order());
        Assert.DoesNotContain(bookmarks, item => item.CollectionId == -99);
    }

    [IntegrationFact]
    public async Task Collections_support_create_rename_reparent_and_move_to_root()
    {
        var root = await account.CreateCollectionAsync("tree-root");
        var other = await account.CreateCollectionAsync("tree-other");
        var child = await account.CreateCollectionAsync("tree-child", root.Id);
        Assert.Null(root.ParentId);
        Assert.Equal(root.Id, child.ParentId);

        var newTitle = account.CollectionTitle("tree-renamed-Привет-🌧");
        var moved = await account.Client.UpdateCollectionAsync(child.Id, new(newTitle, other.Id));
        Assert.Equal(newTitle, moved.Title);
        Assert.Equal(other.Id, moved.ParentId);
        var promoted = await account.Client.UpdateCollectionAsync(child.Id, new(newTitle));
        Assert.Null(promoted.ParentId);
        var raw = (await account.RawAsync(HttpMethod.Get, $"collection/{child.Id}")).GetProperty("item");
        Assert.Equal(newTitle, raw.GetProperty("title").GetString());
        Assert.True(!raw.TryGetProperty("parent", out var parent) || parent.ValueKind == JsonValueKind.Null,
            "The server did not move the collection to the root.");
        Assert.Contains(await account.Client.GetCollectionsAsync(), item => item.Id == child.Id && item.ParentId is null);
    }

    [IntegrationFact]
    public async Task Identically_named_sibling_collections_keep_distinct_IDs()
    {
        var root = await account.CreateCollectionAsync("duplicate-folder-root");
        var first = await account.CreateCollectionAsync("duplicate-folder", root.Id);
        var second = await account.CreateCollectionAsync("duplicate-folder", root.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Title, second.Title);
        var actual = (await account.Client.GetCollectionsAsync()).Where(item => item.ParentId == root.Id).ToArray();
        Assert.Equal(new[] { first.Id, second.Id }.Order(), actual.Select(item => item.Id).Order());
    }

    [IntegrationFact]
    public async Task Explicit_collection_deletion_removes_nested_and_empty_collections_only()
    {
        var root = await account.CreateCollectionAsync("delete-root");
        var child = await account.CreateCollectionAsync("delete-child", root.Id);
        var empty = await account.CreateCollectionAsync("delete-empty");
        var keeper = await account.CreateCollectionAsync("delete-keeper");
        var bookmark = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(child.Id, "deleted-with-folder")));
        await account.Client.DeleteCollectionsAsync([root, child, empty]);
        var collections = await account.Client.GetCollectionsAsync();
        Assert.DoesNotContain(collections, item => new[] { root.Id, child.Id, empty.Id }.Contains(item.Id));
        Assert.Contains(collections, item => item.Id == keeper.Id);
        Assert.Equal(-99, CollectionId(await account.ReadBookmarkRawAsync(bookmark.Id)));
    }

    [IntegrationFact]
    public async Task Individual_bookmark_update_preserves_ID_and_changes_title_URL_and_collection()
    {
        var source = await account.CreateCollectionAsync("individual-source");
        var target = await account.CreateCollectionAsync("individual-target");
        var created = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(source.Id, "individual")));
        var write = account.Bookmark(target.Id, "individual-changed") with { Title = "Привет 🌧 & bookmark" };
        var updated = await account.Client.UpdateBookmarkAsync(created.Id, write);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(write.Title, updated.Title);
        Assert.Equal(write.Link, updated.Link);
        Assert.Equal(target.Id, updated.CollectionId);
        var raw = await account.ReadBookmarkRawAsync(created.Id);
        Assert.Equal(write.Title, raw.GetProperty("title").GetString());
        Assert.Equal(write.Link, raw.GetProperty("link").GetString());
        Assert.Equal(target.Id, CollectionId(raw));
    }

    [IntegrationFact]
    public async Task HTML_like_bookmark_titles_return_the_server_normalized_value()
    {
        var folder = await account.CreateCollectionAsync("title-round-trip");
        var created = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(folder.Id, "title-round-trip")));
        var write = account.Bookmark(folder.Id, "title-round-trip") with { Title = "Привет 🌧 & <bookmark>" };
        var updated = await account.Client.UpdateBookmarkAsync(created.Id, write);
        var raw = await account.ReadBookmarkRawAsync(created.Id);
        // This is a client contract check, not a promise of lossless XBEL synchronization.
        // The client must return the stored server value without pretending the input survived.
        Assert.Equal(raw.GetProperty("title").GetString(), updated.Title);
        Assert.Equal("Привет 🌧 & bookmark", updated.Title);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(write.Link, updated.Link);
        Assert.Equal(folder.Id, updated.CollectionId);
    }

    [IntegrationFact]
    public async Task Bookmark_update_does_not_overwrite_Raindrop_only_metadata()
    {
        var folder = await account.CreateCollectionAsync("metadata");
        var created = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(folder.Id, "metadata")));
        await account.RawAsync(HttpMethod.Put, $"raindrop/{created.Id}", new
        {
            tags = new[] { account.Prefix + "tag" },
            note = "Keep this note",
            excerpt = "Keep this description",
            important = true
        });
        var before = await account.ReadBookmarkRawAsync(created.Id);
        Assert.True(before.GetProperty("important").GetBoolean());
        await account.Client.UpdateBookmarkAsync(created.Id, account.Bookmark(folder.Id, "metadata-updated"));
        var after = await account.ReadBookmarkRawAsync(created.Id);
        foreach (var field in new[] { "tags", "note", "excerpt", "important" })
        {
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        }
    }

    [IntegrationFact]
    public async Task Bulk_101_bookmarks_round_trip_through_pagination_move_and_trash()
    {
        var source = await account.CreateCollectionAsync("bulk-source");
        var target = await account.CreateCollectionAsync("bulk-target");
        var keeper = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(source.Id, "bulk-keeper")));
        var inputs = Enumerable.Range(0, 101).Select(i => account.Bookmark(source.Id, $"bulk-{i:D3}")).ToArray();
        var start = account.Observer.Requests.Count;
        var created = new List<RaindropBookmark>();
        var batches = new List<int>();
        await foreach (var batch in account.Client.CreateBookmarksAsync(inputs))
        {
            batches.Add(batch.Count);
            created.AddRange(batch);
        }

        Assert.Equal(new[] { 100, 1 }, batches);
        AssertBatchSizes(start, "POST", "/rest/v1/raindrops", 100, 1);
        Assert.Equal(101, created.Select(item => item.Id).Distinct().Count());
        var ids = created.Select(item => item.Id).ToHashSet();
        Assert.Equal(inputs.Select(item => item.Link).Order(), created.Select(item => item.Link).Order());

        start = account.Observer.Requests.Count;
        var snapshot = await account.Client.GetActiveBookmarksAsync();
        var actual = snapshot.Where(item => ids.Contains(item.Id)).ToArray();
        Assert.Equal(101, actual.Length);
        Assert.All(actual, item => Assert.Equal(source.Id, item.CollectionId));
        Assert.Equal(created.OrderBy(item => item.Id), actual.OrderBy(item => item.Id));
        Assert.Contains(account.Observer.Requests.Skip(start), r => r.Method == "GET" && r.PathAndQuery.Contains("page=2&"));

        start = account.Observer.Requests.Count;
        await account.Client.MoveBookmarksAsync(source.Id, target.Id, actual);
        AssertBatchSizes(start, "PUT", $"/rest/v1/raindrops/{source.Id}", 100, 1);
        snapshot = await account.Client.GetActiveBookmarksAsync();
        Assert.Equal(101, snapshot.Count(item => ids.Contains(item.Id) && item.CollectionId == target.Id));
        Assert.Contains(snapshot, item => item.Id == keeper.Id && item.CollectionId == source.Id);

        start = account.Observer.Requests.Count;
        await account.Client.TrashBookmarksAsync(target.Id,
            snapshot.Where(item => ids.Contains(item.Id)).ToArray());
        AssertBatchSizes(start, "DELETE", $"/rest/v1/raindrops/{target.Id}", 100, 1);
        Assert.DoesNotContain(await account.Client.GetActiveBookmarksAsync(), item => ids.Contains(item.Id));
        Assert.Equal(-99, CollectionId(await account.ReadBookmarkRawAsync(created[0].Id)));
        Assert.Equal(-99, CollectionId(await account.ReadBookmarkRawAsync(created[^1].Id)));
        Assert.Equal(source.Id, CollectionId(await account.ReadBookmarkRawAsync(keeper.Id)));
    }

    [IntegrationFact]
    public async Task Duplicate_URLs_in_same_and_different_collections_are_not_merged()
    {
        var first = await account.CreateCollectionAsync("duplicate-url-first");
        var second = await account.CreateCollectionAsync("duplicate-url-second");
        var duplicate = account.Bookmark(first.Id, "duplicate-url");
        var created = await account.CreateBookmarksAsync(duplicate, duplicate, duplicate with { CollectionId = second.Id });
        Assert.Equal(3, created.Select(item => item.Id).Distinct().Count());
        var actual = (await account.Client.GetActiveBookmarksAsync()).Where(item => item.Link == duplicate.Link).ToArray();
        Assert.Equal(3, actual.Length);
        Assert.Equal(2, actual.Count(item => item.CollectionId == first.Id));
        Assert.Single(actual, item => item.CollectionId == second.Id);
    }

    [IntegrationFact]
    public async Task Unsorted_supports_creation_read_move_and_trash()
    {
        var folder = await account.CreateCollectionAsync("unsorted-target");
        var created = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(-1, "unsorted")));
        Assert.Equal(-1, created.CollectionId);
        Assert.Contains(await account.Client.GetActiveBookmarksAsync(), item => item.Id == created.Id && item.CollectionId == -1);
        await account.Client.MoveBookmarksAsync(-1, folder.Id, [created]);
        Assert.Equal(folder.Id, CollectionId(await account.ReadBookmarkRawAsync(created.Id)));
        await account.Client.MoveBookmarksAsync(folder.Id, -1, [created]);
        Assert.Equal(-1, CollectionId(await account.ReadBookmarkRawAsync(created.Id)));
        await account.Client.TrashBookmarksAsync(-1, [created]);
        Assert.Equal(-99, CollectionId(await account.ReadBookmarkRawAsync(created.Id)));
    }

    [IntegrationFact]
    public async Task Repeating_scoped_trash_does_not_permanently_delete_a_bookmark()
    {
        var folder = await account.CreateCollectionAsync("safe-trash");
        var created = Assert.Single(await account.CreateBookmarksAsync(account.Bookmark(folder.Id, "safe-trash")));
        await account.Client.TrashBookmarksAsync(folder.Id, [created]);
        await account.Client.TrashBookmarksAsync(folder.Id, [created]);
        Assert.Equal(-99, CollectionId(await account.ReadBookmarkRawAsync(created.Id)));
        Assert.DoesNotContain(await account.Client.GetActiveBookmarksAsync(), item => item.Id == created.Id);
    }

    [IntegrationFact]
    public async Task Cancellation_after_confirmed_batch_does_not_send_the_next_batch()
    {
        var folder = await account.CreateCollectionAsync("cancel-batch");
        var inputs = Enumerable.Range(0, 101).Select(i => account.Bookmark(folder.Id, $"cancel-{i:D3}")).ToArray();
        using var stop = new CancellationTokenSource();
        var confirmed = new List<long>();
        var start = account.Observer.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in account.Client.CreateBookmarksAsync(inputs, stop.Token))
            {
                confirmed.AddRange(batch.Select(item => item.Id));
                stop.Cancel();
            }
        });
        Assert.Equal(100, confirmed.Count);
        AssertBatchSizes(start, "POST", "/rest/v1/raindrops", 100);
        var actual = (await account.Client.GetActiveBookmarksAsync()).Where(item => item.CollectionId == folder.Id).ToArray();
        Assert.Equal(confirmed.Order(), actual.Select(item => item.Id).Order());
        Assert.DoesNotContain(actual, item => item.Link == inputs[^1].Link);
    }

    [IntegrationFact]
    public async Task Invalid_token_returns_a_sanitized_authentication_error()
    {
        var invalidConfiguration = new IntegrationTestConfiguration("deliberately-invalid-integration-token");
        var client = new RaindropApiClient(account.Factory, invalidConfiguration, new TestLogger());
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => client.GetCollectionsAsync());
        Assert.True(error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.True(error.IsAuthenticationFailure);
        Assert.False(error.IsTransient);
        Assert.False(error.OutcomeMayBeUnknown);
        Assert.True(!error.ToString().Contains(invalidConfiguration.RaindropApiToken, StringComparison.Ordinal), "Exception leaked a token.");
        Assert.True(!error.ToString().Contains(account.Configuration.RaindropApiToken, StringComparison.Ordinal), "Exception leaked a token.");
    }

    [IntegrationFact]
    public async Task Empty_inputs_and_safety_guards_send_no_HTTP_requests()
    {
        var start = account.Observer.Requests.Count;
        Assert.Empty(await account.CreateBookmarksAsync());
        await account.Client.DeleteCollectionsAsync([]);
        await account.Client.MoveBookmarksAsync(-1, 1, []);
        await account.Client.TrashBookmarksAsync(-1, []);
        var bookmark = new RaindropBookmark(1, -1, "bookmark", "https://example.com");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => account.Client.TrashBookmarksAsync(-99, [bookmark]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => account.Client.TrashBookmarksAsync(0, [bookmark]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => account.Client.MoveBookmarksAsync(-1, -99, [bookmark]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => account.Client.DeleteCollectionsAsync(
            [new RaindropCollection(1, null, "valid"), new RaindropCollection(-99, null, "invalid")]));
        await Assert.ThrowsAsync<ArgumentException>(() => account.Client.TrashBookmarksAsync(-1, [bookmark, bookmark]));
        await Assert.ThrowsAsync<ArgumentException>(() => account.Client.UpdateCollectionAsync(1, new("self", 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => account.CreateBookmarksAsync(
            account.Bookmark(-1, "valid-but-must-not-be-sent"), account.Bookmark(-99, "invalid")));
        Assert.Equal(start, account.Observer.Requests.Count);
    }

    [IntegrationFact]
    public async Task Pre_cancelled_reads_and_writes_send_no_HTTP_requests()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var start = account.Observer.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => account.Client.GetCollectionsAsync(stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => account.Client.GetActiveBookmarksAsync(stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => account.Client.CreateCollectionAsync(new(account.Prefix + "never-created"), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => account.Client.TrashBookmarksAsync(-1,
            [new RaindropBookmark(1, -1, "never-removed", "https://example.com")], stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in account.Client.CreateBookmarksAsync([account.Bookmark(-1, "never-created")], stop.Token))
            {
                Assert.Fail("A cancelled write yielded a batch.");
            }
        });
        Assert.Equal(start, account.Observer.Requests.Count);
    }

    private void AssertBatchSizes(int start, string method, string path, params int[] expected) =>
        Assert.Equal(expected.Select(value => (int?)value), account.Observer.Requests.Skip(start)
            .Where(request => request.Method == method && request.PathAndQuery == path).Select(request => request.BatchCount));

    private static long Id(JsonElement item) => item.GetProperty("_id").GetInt64();
    private static long CollectionId(JsonElement item) => item.GetProperty("collection").GetProperty("$id").GetInt64();
}
