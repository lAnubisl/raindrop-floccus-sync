using System.Net;
using System.Text.Json;
using RaindropToFloccus.Models;
using Xunit;
using static RaindropToFloccus.IntegrationTests.OfflineRaindropData;

namespace RaindropToFloccus.IntegrationTests;

[Trait("Category", "RaindropContract")]
public sealed class RaindropBatchAndPaginationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 3)]
    public async Task Pagination_stops_at_the_first_short_page_including_an_extra_empty_page(int total, int requests)
    {
        var pages = Ids(1, total).Chunk(50).Select(Items).ToList();
        if (total % 50 == 0) pages.Add(Items([]));
        using var http = new ScriptedHttpClientFactory(pages.ToArray());
        var actual = await Client(http).GetActiveBookmarksAsync();
        Assert.Equal(Ids(1, total), actual.Select(item => item.Id));
        Assert.Equal(requests, http.Paths.Count);
        Assert.Equal(Enumerable.Range(0, requests).Select(page => $"/rest/v1/raindrops/0?perpage=50&page={page}&sort=created"), http.PathsAndQueries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repeated_page_or_single_overlapping_ID_rejects_the_entire_snapshot(bool fullRepeat)
    {
        using var http = new ScriptedHttpClientFactory(Items(Ids(1, 50)), Items(fullRepeat ? Ids(1, 50) : [50]));
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).GetActiveBookmarksAsync());
        Assert.True(error.IsTransient);
        Assert.False(error.OutcomeMayBeUnknown);
        Assert.Equal(2, http.Paths.Count);
    }

    [Fact]
    public async Task Oversized_page_is_rejected()
    {
        using var http = new ScriptedHttpClientFactory(Items(Ids(1, 51)));
        await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).GetActiveBookmarksAsync());
        Assert.Single(http.Paths);
    }

    [Fact]
    public async Task Unexpected_trash_items_are_filtered_without_terminating_pagination_early()
    {
        var trashPage = JsonSerializer.Serialize(new { result = true, items = Ids(1, 50).Select(id => Bookmark(id, -99)) });
        using var http = new ScriptedHttpClientFactory(trashPage, Items([51]));
        var actual = Assert.Single(await Client(http).GetActiveBookmarksAsync());
        Assert.Equal(51, actual.Id);
        Assert.Equal(-1, actual.CollectionId);
        Assert.Equal(2, http.Paths.Count);
    }

    [Theory]
    [InlineData("503")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("short-response")]
    [InlineData("duplicate-ID")]
    public async Task A_failed_second_creation_batch_preserves_confirmed_results_and_never_sends_a_third(string failure)
    {
        var calls = 0;
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Items(Ids(1, 100))));
                return failure switch
                {
                    "network" => throw new HttpRequestException(Token),
                    "timeout" => throw new TaskCanceledException(Token),
                    "short-response" => Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Items([]))),
                    "duplicate-ID" => Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Items(Ids(100, 100)))),
                    _ => Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Token, HttpStatusCode.ServiceUnavailable))
                };
            }
        };
        var confirmed = new List<long>();
        var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
        {
            await foreach (var batch in Client(http).CreateBookmarksAsync(Inputs(201)))
                confirmed.AddRange(batch.Select(item => item.Id));
        });
        Assert.Equal(Ids(1, 100), confirmed);
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.True(error.IsTransient);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(2, calls);
        Assert.All(http.RequestBodies, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(100, json.RootElement.GetProperty("items").GetArrayLength());
        });
    }

    [Theory]
    [InlineData("move")]
    [InlineData("trash")]
    [InlineData("collections")]
    public async Task Failure_in_second_mutation_batch_stops_without_retrying_or_sending_the_third(string operation)
    {
        var calls = 0;
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) => Task.FromResult(++calls == 1
                ? ScriptedHttpClientFactory.JsonResponse("{\"result\":true,\"modified\":100}")
                : ScriptedHttpClientFactory.JsonResponse("{}", HttpStatusCode.ServiceUnavailable))
        };
        var client = Client(http);
        var ids = Ids(1, 201).ToArray();
        var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
        {
            if (operation == "move") await client.MoveBookmarksAsync(-1, 2, ids);
            else if (operation == "trash") await client.TrashBookmarksAsync(-1, ids);
            else await client.DeleteCollectionsAsync(ids);
        });
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.Equal(2, calls);
        using var first = JsonDocument.Parse(http.RequestBodies[0]);
        using var second = JsonDocument.Parse(http.RequestBodies[1]);
        Assert.Equal(Ids(1, 100), first.RootElement.GetProperty("ids").EnumerateArray().Select(item => item.GetInt64()));
        Assert.Equal(Ids(101, 100), second.RootElement.GetProperty("ids").EnumerateArray().Select(item => item.GetInt64()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Incomplete_bulk_move_must_not_be_reported_as_unqualified_success(int modified)
    {
        // Desired safety behavior: the response explicitly says fewer than both requested
        // bookmarks were changed. Keep this regression red if the client silently ignores it.
        // This is not applied to DELETE, where a repeated request can legitimately modify zero.
        using var http = new ScriptedHttpClientFactory($"{{\"result\":true,\"modified\":{modified}}}");
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).MoveBookmarksAsync(-1, 2, [1, 2]));
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }

    [Fact]
    public async Task A_deleted_destination_is_reported_without_recreating_it_or_repeating_the_move()
    {
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) => Task.FromResult(ScriptedHttpClientFactory.JsonResponse("{}", HttpStatusCode.NotFound))
        };
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).MoveBookmarksAsync(-1, 2, [1]));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.False(error.IsTransient);
        Assert.Equal(new[] { "PUT" }, http.Methods);
        Assert.Equal(new[] { "/rest/v1/raindrops/-1" }, http.Paths);
        using var body = JsonDocument.Parse(Assert.Single(http.RequestBodies));
        Assert.Equal(2, body.RootElement.GetProperty("collection").GetProperty("$id").GetInt64());
    }

    [Fact]
    public async Task Duplicate_created_IDs_within_one_batch_are_rejected_before_yielding()
    {
        using var http = new ScriptedHttpClientFactory(Items([1, 1]));
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => CreateAsync(Client(http), 2));
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }

    [Fact]
    public async Task Repeated_scoped_trash_accepts_zero_modified_without_permanent_deletion()
    {
        using var http = new ScriptedHttpClientFactory("{\"result\":true,\"modified\":0}");
        await Client(http).TrashBookmarksAsync(-1, [1]);
        Assert.Equal(new[] { "/rest/v1/raindrops/-1" }, http.Paths);
        Assert.Equal(new[] { "DELETE" }, http.Methods);
        using var request = JsonDocument.Parse(Assert.Single(http.RequestBodies));
        Assert.Equal(1, request.RootElement.GetProperty("ids")[0].GetInt64());
    }

    [Theory]
    [InlineData("{\"result\":true,\"items\":[]}")]
    [InlineData("{\"result\":true,\"items\":[{\"_id\":1,\"title\":\"x\",\"link\":\"https://example.com\",\"collection\":{\"$id\":-99}}]}")]
    [InlineData("{\"result\":true,\"items\":[{\"_id\":0,\"title\":\"x\",\"link\":\"https://example.com\",\"collection\":{\"$id\":-1}}]}")]
    public async Task Incomplete_or_invalid_creation_results_are_never_yielded(string response)
    {
        using var http = new ScriptedHttpClientFactory(response);
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => CreateAsync(Client(http), 1));
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }
}
