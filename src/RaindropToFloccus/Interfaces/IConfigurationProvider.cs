using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface IConfigurationProvider
{
    string GitRepositoryUrl { get; }

    string GitSshPrivateKey { get; }

    string RaindropApiToken { get; }

    TimeSpan RaindropRequestTimeout { get; }

    string GitAuthorName { get; }

    string GitAuthorEmail { get; }

    string GitWorkingDirectory { get; }

    TimeSpan SynchronizationInterval { get; }

    TimeSpan RetryInterval { get; }

    int MaximumRetryAttempts { get; }

    ApplicationLogLevel LogLevel { get; }

    int HealthCheckPort { get; }
}
