using System.Text.Json;
using RaindropToFloccus.Clients;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.IntegrationTests;

[Trait("Category", "RaindropContract")]
public sealed class RaindropClientContractTests
{
    [Fact]
    public async Task Identical_collection_IDs_across_endpoints_are_returned_once()
    {
        using var http = new ScriptedHttpClientFactory(
            """{"result":true,"items":[{"_id":1,"title":"root"},{"_id":2,"title":"child","parent":{"$id":1}}]}""",
            """{"result":true,"items":[{"_id":1,"title":"root","parent":null},{"_id":2,"title":"child","parent":{"$id":1}}]}""");
        var client = new RaindropApiClient(http, new IntegrationTestConfiguration("offline-token"), new TestLogger());
        var actual = await client.GetCollectionsAsync();
        Assert.Equal(new[] { new RaindropCollection(1, null, "root"), new RaindropCollection(2, 1, "child") }, actual);
        Assert.Equal(new[] { "/rest/v1/collections", "/rest/v1/collections/childrens" }, http.Paths);
    }

    [Theory]
    [InlineData("changed", 1)]
    [InlineData("child", 3)]
    public async Task Conflicting_title_or_parent_for_the_same_ID_is_rejected(string title, long parentId)
    {
        var second = JsonSerializer.Serialize(new
        {
            result = true,
            items = new[] { new { _id = 2, title, parent = new Dictionary<string, long> { ["$id"] = parentId } } }
        });
        using var http = new ScriptedHttpClientFactory(
            """{"result":true,"items":[{"_id":2,"title":"child","parent":{"$id":1}}]}""", second);
        var client = new RaindropApiClient(http, new IntegrationTestConfiguration("offline-token"), new TestLogger());
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => client.GetCollectionsAsync());
        Assert.True(error.IsTransient);
        Assert.False(error.OutcomeMayBeUnknown);
        Assert.Equal("read collections", error.Operation);
    }

    [Fact]
    public async Task Different_IDs_with_identical_titles_and_parents_stay_separate()
    {
        using var http = new ScriptedHttpClientFactory(
            """{"result":true,"items":[{"_id":1,"title":"root"},{"_id":2,"title":"same","parent":{"$id":1}}]}""",
            """{"result":true,"items":[{"_id":2,"title":"same","parent":{"$id":1}},{"_id":3,"title":"same","parent":{"$id":1}}]}""");
        var client = new RaindropApiClient(http, new IntegrationTestConfiguration("offline-token"), new TestLogger());
        var actual = await client.GetCollectionsAsync();
        Assert.Equal(new long[] { 1, 2, 3 }, actual.Select(item => item.Id));
        Assert.Equal(2, actual.Count(item => item.Title == "same" && item.ParentId == 1));
    }

    [Theory]
    [InlineData("Привет 🌧 & bookmark")]
    [InlineData("Привет 🌧 & <bookmark>")]
    public async Task Update_sends_original_title_and_returns_server_title_without_local_normalization(string storedTitle)
    {
        const string originalTitle = "Привет 🌧 & <bookmark>";
        const string link = "https://example.com/title";
        var response = JsonSerializer.Serialize(new
        {
            result = true,
            item = new { _id = 42, title = storedTitle, link, collection = new Dictionary<string, long> { ["$id"] = -1 } }
        });
        using var http = new ScriptedHttpClientFactory(response);
        var logger = new TestLogger();
        var client = new RaindropApiClient(http, new IntegrationTestConfiguration("offline-token"), logger);
        var actual = await client.UpdateBookmarkAsync(42, new(-1, originalTitle, link));
        Assert.Equal(storedTitle, actual.Title);
        Assert.Equal(42, actual.Id);
        Assert.Equal($"Changing bookmark \"{originalTitle}\" in Raindrop.", Assert.Single(logger.Information));
        using var body = JsonDocument.Parse(Assert.Single(http.RequestBodies));
        Assert.Equal(originalTitle, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(link, body.RootElement.GetProperty("link").GetString());
        Assert.Equal(new[] { "/rest/v1/raindrop/42" }, http.Paths);
    }
}
