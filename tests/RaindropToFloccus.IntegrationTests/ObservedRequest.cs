namespace RaindropToFloccus.IntegrationTests;

// Never store headers, tokens, request bodies or response bodies in diagnostic traces.
public sealed record ObservedRequest(string Method, string PathAndQuery, int? BatchCount, int StatusCode);
