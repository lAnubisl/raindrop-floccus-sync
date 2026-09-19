using System.Net;
using System.Text;

namespace RaindropToFloccus.IntegrationTests;

// Offline contract tests only: no sockets or live credentials are used.
public sealed class ScriptedHttpClientFactory(params string[] responses) : HttpMessageHandler, IHttpClientFactory
{
    private readonly Queue<string> _responses = new(responses);
    public List<string> Paths { get; } = [];
    public List<string> PathsAndQueries { get; } = [];
    public List<string> Methods { get; } = [];
    public List<string> RequestBodies { get; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; init; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { Timeout = Timeout };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        PathsAndQueries.Add(request.RequestUri.PathAndQuery);
        Methods.Add(request.Method.Method);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return RespondAsync is not null
            ? await RespondAsync(request, cancellationToken)
            : JsonResponse(_responses.Dequeue());
    }

    public static HttpResponseMessage JsonResponse(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}
