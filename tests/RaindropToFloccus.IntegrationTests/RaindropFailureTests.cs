using System.Globalization;
using System.Net;
using RaindropToFloccus.Models;
using Xunit;
using static RaindropToFloccus.IntegrationTests.OfflineRaindropData;

namespace RaindropToFloccus.IntegrationTests;

[Trait("Category", "RaindropContract")]
public sealed class RaindropFailureTests
{
    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public async Task HTTP_errors_classify_reads_and_writes_without_retries_or_secret_leaks(int status, bool transient)
    {
        foreach (var write in new[] { false, true })
        {
            using var http = new ScriptedHttpClientFactory
            {
                RespondAsync = (_, _) => Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Token, (HttpStatusCode)status))
            };
            var client = Client(http);
            var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
            {
                if (write) await client.UpdateBookmarkAsync(1, Inputs(1)[0]);
                else await client.GetActiveBookmarksAsync();
            });
            Assert.Equal((HttpStatusCode)status, error.StatusCode);
            Assert.Equal(transient, error.IsTransient);
            Assert.Equal(status is 401 or 403, error.IsAuthenticationFailure);
            Assert.Equal(write && transient, error.OutcomeMayBeUnknown);
            Assert.DoesNotContain(Token, error.ToString());
            Assert.Null(error.InnerException);
            Assert.Single(http.Paths);
        }
    }

    [Fact]
    public async Task Rate_limited_writes_wait_for_the_reset_and_retry_the_rejected_request()
    {
        var responses = 0;
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) =>
            {
                responses++;
                if (responses == 1)
                {
                    var rateLimited = ScriptedHttpClientFactory.JsonResponse("{}", HttpStatusCode.TooManyRequests);
                    rateLimited.Headers.TryAddWithoutValidation("X-RateLimit-Reset",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                    return Task.FromResult(rateLimited);
                }

                return Task.FromResult(ScriptedHttpClientFactory.JsonResponse(Item()));
            }
        };

        var bookmark = await Client(http).UpdateBookmarkAsync(1, Inputs(1)[0]);

        Assert.Equal(1, bookmark.Id);
        Assert.Equal(2, http.Paths.Count);
        Assert.All(http.Methods, method => Assert.Equal("PUT", method));
        Assert.Equal(http.RequestBodies[0], http.RequestBodies[1]);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task Transport_failures_are_sanitized_and_write_outcomes_are_unknown(string failure)
    {
        foreach (var write in new[] { false, true })
        {
            using var http = new ScriptedHttpClientFactory
            {
                RespondAsync = (_, _) => throw failure switch
                {
                    "network" => new HttpRequestException(Token),
                    "io" => new IOException(Token),
                    _ => new TaskCanceledException(Token)
                }
            };
            var client = Client(http);
            var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
            {
                if (write) await client.UpdateBookmarkAsync(1, Inputs(1)[0]);
                else await client.GetActiveBookmarksAsync();
            });
            Assert.True(error.IsTransient);
            Assert.Equal(write, error.OutcomeMayBeUnknown);
            Assert.DoesNotContain(Token, error.ToString());
            Assert.Null(error.InnerException);
            Assert.Single(http.Paths);
        }
    }

    [Theory]
    [InlineData("Retry-After", "30", 30)]
    [InlineData("Retry-After", "0", 0)]
    [InlineData("Retry-After", "not-a-delay", -1)]
    [InlineData("X-RateLimit-Reset", "0", 0)]
    [InlineData("X-RateLimit-Reset", "invalid", -1)]
    [InlineData("X-RateLimit-Reset", "9223372036854775807", -1)]
    public async Task Retry_delay_handles_seconds_expired_and_invalid_headers(string name, string value, int expectedSeconds)
    {
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) =>
            {
                var response = ScriptedHttpClientFactory.JsonResponse("{}", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(name, value);
                return Task.FromResult(response);
            }
        };
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).GetActiveBookmarksAsync());
        Assert.Equal(expectedSeconds < 0 ? null : (TimeSpan?)TimeSpan.FromSeconds(expectedSeconds), error.RetryAfter);
        Assert.True(error.IsTransient);
        Assert.Single(http.Paths);
    }

    [Theory]
    [InlineData("Retry-After", false)]
    [InlineData("X-RateLimit-Reset", false)]
    [InlineData("Retry-After", true)]
    public async Task Retry_delay_handles_dates_and_prefers_Retry_After(string name, bool addReset)
    {
        var target = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds());
        var before = DateTimeOffset.UtcNow;
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) =>
            {
                var response = ScriptedHttpClientFactory.JsonResponse("{}", HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation(name, name == "Retry-After"
                    ? target.ToString("R", CultureInfo.InvariantCulture)
                    : target.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                if (addReset) response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "0");
                return Task.FromResult(response);
            }
        };
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).GetActiveBookmarksAsync());
        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter.Value, target - DateTimeOffset.UtcNow, target - before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"result\":true}")]
    [InlineData("{\"result\":\"true\",\"items\":[]}")]
    [InlineData("{\"result\":true,\"items\":null}")]
    [InlineData("{\"result\":true,\"items\":{}}")]
    [InlineData("{\"result\":true,\"items\":[null]}")]
    [InlineData("{\"result\":true,\"items\":[{\"_id\":1,\"collection\":{\"$id\":-1},\"title\":\"x\"}]}")]
    public async Task Malformed_reads_never_turn_into_a_successful_empty_snapshot(string body)
    {
        using var http = new ScriptedHttpClientFactory(body);
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).GetActiveBookmarksAsync());
        Assert.True(error.IsTransient);
        Assert.False(error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }

    [Fact]
    public async Task Result_false_is_not_success_even_with_HTTP_200_and_an_empty_array()
    {
        foreach (var write in new[] { false, true })
        {
            using var http = new ScriptedHttpClientFactory($"{{\"result\":false,\"items\":[],\"errorMessage\":\"{Token}\"}}");
            var client = Client(http);
            var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
            {
                if (write) await client.TrashBookmarksAsync(-1, [ExistingBookmark(1)]);
                else await client.GetActiveBookmarksAsync();
            });
            Assert.False(error.IsTransient);
            Assert.Equal(write, error.OutcomeMayBeUnknown);
            Assert.DoesNotContain(Token, error.ToString());
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("create")]
    [InlineData("update")]
    public async Task No_content_is_rejected_when_a_snapshot_or_written_item_is_required(string operation)
    {
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
        };
        var client = Client(http);
        var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
        {
            if (operation == "read") await client.GetActiveBookmarksAsync();
            else if (operation == "create") await CreateAsync(client, 1);
            else await client.UpdateBookmarkAsync(1, Inputs(1)[0]);
        });
        Assert.Equal(operation != "read", error.OutcomeMayBeUnknown);
    }

    [Fact]
    public async Task No_content_is_allowed_for_bulk_operations_without_returned_items()
    {
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
        };
        var client = Client(http);
        await client.MoveBookmarksAsync(-1, 2, [ExistingBookmark(1)]);
        await client.TrashBookmarksAsync(-1, [ExistingBookmark(1)]);
        await client.DeleteCollectionsAsync([ExistingCollection(2)]);
        Assert.Equal(3, http.Paths.Count);
    }

    [Theory]
    [InlineData(99, -1)]
    [InlineData(1, -99)]
    public async Task Wrong_bookmark_ID_or_trashed_update_response_is_rejected(long id, long collection)
    {
        using var http = new ScriptedHttpClientFactory(Item(id, collection));
        var error = await Assert.ThrowsAsync<RaindropApiException>(() => Client(http).UpdateBookmarkAsync(1, Inputs(1)[0]));
        Assert.True(error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }
}
