using System.Text.Json;
using RaindropToFloccus.Models;
using Xunit;
using static RaindropToFloccus.IntegrationTests.OfflineRaindropData;

namespace RaindropToFloccus.IntegrationTests;

[Trait("Category", "RaindropContract")]
public sealed class RaindropCancellationAndInputTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Cancellation_during_a_read_reaches_the_transport_and_remains_cancellation()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = async (_, token) =>
            {
                using var registration = token.Register(() => observed.TrySetResult());
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Read should have been cancelled.");
            }
        };
        var operation = Client(http).GetActiveBookmarksAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(TestDeadline);
            stop.Cancel();
            await observed.Task.WaitAsync(TestDeadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestDeadline));
            Assert.Single(http.Paths);
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public async Task Cancellation_during_a_write_allows_a_confirmed_response()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new ScriptedHttpClientFactory
        {
            RespondAsync = async (_, token) =>
            {
                entered.TrySetResult(token);
                await release.Task.WaitAsync(token);
                return ScriptedHttpClientFactory.JsonResponse(Item());
            }
        };
        var operation = Client(http).UpdateBookmarkAsync(1, Inputs(1)[0], stop.Token);
        try
        {
            var transportToken = await entered.Task.WaitAsync(TestDeadline);
            stop.Cancel();
            Assert.False(transportToken.IsCancellationRequested);
            Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var item = await operation.WaitAsync(TestDeadline);
            Assert.Equal(1, item.Id);
            Assert.Single(http.Paths);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_HttpClient_timeout_bounds_a_hanging_request_and_marks_write_uncertainty(bool write)
    {
        using var http = new ScriptedHttpClientFactory
        {
            Timeout = TimeSpan.FromMilliseconds(200),
            RespondAsync = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The transport should time out.");
            }
        };
        var client = Client(http);
        var error = await Assert.ThrowsAsync<RaindropApiException>(async () =>
        {
            if (write) await client.UpdateBookmarkAsync(1, Inputs(1)[0]).WaitAsync(TestDeadline);
            else await client.GetActiveBookmarksAsync().WaitAsync(TestDeadline);
        });
        Assert.True(error.IsTransient);
        Assert.Equal(write, error.OutcomeMayBeUnknown);
        Assert.Single(http.Paths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(1000)]
    public async Task Titles_within_the_client_limit_are_sent_without_truncation(int length)
    {
        var title = new string('a', length);
        using var http = new ScriptedHttpClientFactory(Item(title: title));
        var actual = await Client(http).UpdateBookmarkAsync(1, new(-1, title, Link));
        using var request = JsonDocument.Parse(Assert.Single(http.RequestBodies));
        Assert.Equal(title, request.RootElement.GetProperty("title").GetString());
        Assert.Equal(title, actual.Title);
    }

    [Fact]
    public async Task Invalid_last_title_in_a_large_input_prevents_all_creation_requests()
    {
        using var http = new ScriptedHttpClientFactory();
        var inputs = Inputs(201);
        inputs[^1] = inputs[^1] with { Title = new string('a', 1001) };
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var batch in Client(http).CreateBookmarksAsync(inputs))
                Assert.Fail("Invalid input must not yield a batch.");
        });
        Assert.Empty(http.Paths);
    }

    [Theory]
    [InlineData("https://example.com/path?a=1&b=%2F#fragment")]
    [InlineData("https://пример.рф/путь?q=🌧")]
    [InlineData("mailto:hello@example.com")]
    public async Task URL_text_is_transported_exactly_without_encoding_or_normalization(string link)
    {
        using var http = new ScriptedHttpClientFactory(Item(link: link));
        var actual = await Client(http).UpdateBookmarkAsync(1, new(-1, "title", link));
        Assert.Equal(link, actual.Link);
        using var body = JsonDocument.Parse(Assert.Single(http.RequestBodies));
        Assert.Equal(link, body.RootElement.GetProperty("link").GetString());
        // This checks client transport fidelity, not whether the live service accepts every scheme.
    }
}
