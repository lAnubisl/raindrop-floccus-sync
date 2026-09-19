using System.Net;

namespace RaindropToFloccus.Models;

public sealed class RaindropApiException : Exception
{
    public RaindropApiException(string operation, string reason, HttpStatusCode? statusCode = null,
        bool isTransient = false, TimeSpan? retryAfter = null, bool outcomeMayBeUnknown = false)
        : base($"Raindrop operation '{operation}' failed: {reason}")
    {
        Operation = operation;
        StatusCode = statusCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
        OutcomeMayBeUnknown = outcomeMayBeUnknown;
    }

    public string Operation { get; }

    public HttpStatusCode? StatusCode { get; }

    public bool IsTransient { get; }

    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public TimeSpan? RetryAfter { get; }

    /// <summary>Re-read/reconcile before retrying a write; it might have reached the server.</summary>
    public bool OutcomeMayBeUnknown { get; }
}
