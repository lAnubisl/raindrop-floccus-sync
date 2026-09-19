using System.Diagnostics;
using System.Text.Json;

namespace RaindropToFloccus.IntegrationTests;

public sealed class LiveHttpObserver : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceRequest = Stopwatch.StartNew();
    public List<ObservedRequest> Requests { get; } = [];

    public LiveHttpObserver()
    {
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Pace every request (including independent verification/cleanup), without retrying writes.
            var remaining = TimeSpan.FromMilliseconds(650) - _sinceRequest.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }

            int? count = null;
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var field in new[] { "items", "ids" })
                {
                    if (body.RootElement.TryGetProperty(field, out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        count = items.GetArrayLength();
                    }
                }
            }

            _sinceRequest.Restart();
            var response = await base.SendAsync(request, cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery, count, (int)response.StatusCode));
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }
}
