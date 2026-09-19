using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.IntegrationTests;

// Test-only configuration: no fake Git credentials or worker startup are needed.
public sealed class IntegrationTestConfiguration : IConfigurationProvider
{
    public static bool IsEnabled => Environment.GetEnvironmentVariable("RAINDROP_RUN_INTEGRATION_TESTS") == "1";

    public IntegrationTestConfiguration(string? tokenOverride = null)
    {
        RaindropApiToken = tokenOverride ?? Environment.GetEnvironmentVariable("RAINDROP_API_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(RaindropApiToken) || RaindropApiToken.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Set a valid RAINDROP_API_TOKEN locally before enabling live tests.");
        }
    }

    public string RaindropApiToken { get; }
    public TimeSpan RaindropRequestTimeout => TimeSpan.FromSeconds(30);
    public string GitRepositoryUrl => throw new NotSupportedException("Git is outside these tests.");
    public string GitSshPrivateKey => throw new NotSupportedException("Git is outside these tests.");
    public string GitAuthorName => throw new NotSupportedException("Git is outside these tests.");
    public string GitAuthorEmail => throw new NotSupportedException("Git is outside these tests.");
    public string GitWorkingDirectory => throw new NotSupportedException("Git is outside these tests.");
    public TimeSpan SynchronizationInterval => TimeSpan.FromHours(1);
    public TimeSpan RetryInterval => TimeSpan.FromMinutes(5);
    public int MaximumRetryAttempts => 5;
    public ApplicationLogLevel LogLevel => ApplicationLogLevel.Error;
    public int HealthCheckPort => 8080;
}
