namespace RaindropToFloccus.IntegrationTests;

public sealed class LiveHttpClientFactory(LiveHttpObserver observer) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(observer, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 16 * 1024 * 1024
    };
}
