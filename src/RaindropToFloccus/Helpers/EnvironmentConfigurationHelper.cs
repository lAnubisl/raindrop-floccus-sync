using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using RaindropToFloccus.Models;
using ApplicationConfigurationProvider = RaindropToFloccus.Interfaces.IConfigurationProvider;

namespace RaindropToFloccus.Helpers;

public sealed partial class EnvironmentConfigurationHelper : ApplicationConfigurationProvider
{
    private static readonly TimeSpan MaximumSchedulingInterval = TimeSpan.FromDays(24);
    private const string GitRepositoryUrlVariable = "GIT_REPOSITORY_URL";
    private const string GitSshPrivateKeyVariable = "GIT_SSH_PRIVATE_KEY";
    private const string RaindropApiTokenVariable = "RAINDROP_API_TOKEN";
    private const string RaindropRequestTimeoutVariable = "RAINDROP_REQUEST_TIMEOUT";
    private const string GitAuthorNameVariable = "GIT_AUTHOR_NAME";
    private const string GitAuthorEmailVariable = "GIT_AUTHOR_EMAIL";
    private const string GitWorkingDirectoryVariable = "GIT_WORKING_DIRECTORY";
    private const string SynchronizationIntervalVariable = "SYNC_INTERVAL";
    private const string RetryIntervalVariable = "RETRY_INTERVAL";
    private const string MaximumRetryAttemptsVariable = "MAX_RETRY_ATTEMPTS";
    private const string LogLevelVariable = "LOG_LEVEL";
    private const string HealthCheckPortVariable = "HEALTH_PORT";

    public EnvironmentConfigurationHelper()
    {
        GitRepositoryUrl = ReadRequired(GitRepositoryUrlVariable);
        GitSshPrivateKey = ReadRequired(GitSshPrivateKeyVariable, trim: false);
        RaindropApiToken = ReadRequired(RaindropApiTokenVariable);
        RaindropRequestTimeout = ReadTimeSpan(RaindropRequestTimeoutVariable, TimeSpan.FromSeconds(30));
        if (RaindropRequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"Environment variable {RaindropRequestTimeoutVariable} must not exceed 00:05:00.");
        }
        GitAuthorName = ReadRequired(GitAuthorNameVariable);
        GitAuthorEmail = ReadRequired(GitAuthorEmailVariable);
        GitWorkingDirectory = ReadWorkingDirectory();
        SynchronizationInterval = ReadTimeSpan(SynchronizationIntervalVariable, TimeSpan.FromHours(1));
        RetryInterval = ReadTimeSpan(RetryIntervalVariable, TimeSpan.FromMinutes(5));
        MaximumRetryAttempts = ReadInteger(MaximumRetryAttemptsVariable, 5, 0, 20);
        LogLevel = ReadLogLevel();
        HealthCheckPort = ReadInteger(HealthCheckPortVariable, 8080, 1, 65535);

        ValidateGitRepositoryUrl(GitRepositoryUrl);
        ValidatePrivateKey(GitSshPrivateKey);
        ValidateSingleLineValue(RaindropApiTokenVariable, RaindropApiToken);
        if (!BearerTokenRegex().IsMatch(RaindropApiToken))
        {
            throw new InvalidOperationException(
                $"Environment variable {RaindropApiTokenVariable} must contain a valid Bearer token.");
        }
        ValidateSingleLineValue(GitAuthorNameVariable, GitAuthorName);
        ValidateEmail(GitAuthorEmail);
        ValidateSchedulingInterval(SynchronizationIntervalVariable, SynchronizationInterval);
        ValidateSchedulingInterval(RetryIntervalVariable, RetryInterval);
    }

    public string GitRepositoryUrl { get; }

    public string GitSshPrivateKey { get; }

    public string RaindropApiToken { get; }

    public TimeSpan RaindropRequestTimeout { get; }

    public string GitAuthorName { get; }

    public string GitAuthorEmail { get; }

    public string GitWorkingDirectory { get; }

    public TimeSpan SynchronizationInterval { get; }

    public TimeSpan RetryInterval { get; }

    public int MaximumRetryAttempts { get; }

    public ApplicationLogLevel LogLevel { get; }

    public int HealthCheckPort { get; }

    private static string ReadRequired(string variableName, bool trim = true)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required environment variable {variableName} is missing.");
        }

        return trim ? value.Trim() : value;
    }

    private static TimeSpan ReadTimeSpan(string variableName, TimeSpan defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result) || result <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Environment variable {variableName} must be a positive time span, for example 01:00:00.");
        }

        return result;
    }

    private static int ReadInteger(string variableName, int defaultValue, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < minimum
            || result > maximum)
        {
            throw new InvalidOperationException(
                $"Environment variable {variableName} must be an integer from {minimum} to {maximum}.");
        }

        return result;
    }

    private static ApplicationLogLevel ReadLogLevel()
    {
        var value = Environment.GetEnvironmentVariable(LogLevelVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return ApplicationLogLevel.Information;
        }

        if (!Enum.TryParse<ApplicationLogLevel>(value, ignoreCase: true, out var result))
        {
            throw new InvalidOperationException(
                $"Environment variable {LogLevelVariable} must be Information, Warning, or Error.");
        }

        return result;
    }

    private static string ReadWorkingDirectory()
    {
        var value = Environment.GetEnvironmentVariable(GitWorkingDirectoryVariable);
        var path = string.IsNullOrWhiteSpace(value)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Path.GetTempPath(), "raindrop-to-floccus", "repository")
                : "/var/lib/raindrop-to-floccus/repository"
            : value.Trim();

        ValidateSingleLineValue(GitWorkingDirectoryVariable, path);

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.GetParent(fullPath) is null)
            {
                throw new InvalidOperationException(
                    $"Environment variable {GitWorkingDirectoryVariable} must not point to a filesystem root.");
            }

            if (File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"Environment variable {GitWorkingDirectoryVariable} must point to a directory, not a file.");
            }

            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Environment variable {GitWorkingDirectoryVariable} must contain a valid directory path.");
        }
    }

    private static void ValidateGitRepositoryUrl(string value)
    {
        var isSshUri = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && !string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/'));

        if (!isSshUri && !ScpStyleGitUrlRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Environment variable {GitRepositoryUrlVariable} must be an SSH Git URL.");
        }
    }

    private static void ValidatePrivateKey(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var lines = normalized.Split('\n');
        if (lines.Length < 3
            || !PrivateKeyHeaderRegex().IsMatch(lines[0])
            || !PrivateKeyFooterRegex().IsMatch(lines[^1]))
        {
            throw new InvalidOperationException(
                $"Environment variable {GitSshPrivateKeyVariable} must contain a multiline PEM private key.");
        }

        var headerLabel = lines[0][11..^5];
        var footerLabel = lines[^1][9..^5];
        if (!string.Equals(headerLabel, footerLabel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment variable {GitSshPrivateKeyVariable} contains mismatched PEM markers.");
        }
    }

    private static void ValidateEmail(string value)
    {
        try
        {
            var address = new MailAddress(value);
            if (!string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Environment variable {GitAuthorEmailVariable} must be a valid email address.");
        }
    }

    private static void ValidateSingleLineValue(string variableName, string value)
    {
        if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Environment variable {variableName} must be a single-line value.");
        }
    }

    private static void ValidateSchedulingInterval(string variableName, TimeSpan value)
    {
        if (value > MaximumSchedulingInterval)
        {
            throw new InvalidOperationException(
                $"Environment variable {variableName} must not exceed {MaximumSchedulingInterval}.");
        }
    }

    [GeneratedRegex(@"^[^@\s]+@[^:\s]+:.+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyleGitUrlRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9._~+/-]+=*\z", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"^-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----$", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyHeaderRegex();

    [GeneratedRegex(@"^-----END (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----$", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyFooterRegex();
}
