namespace RaindropToFloccus.Helpers;

public sealed class GitSshEnvironmentHelper : IGitSshEnvironmentProvider
{
    private readonly ApplicationConfigurationProvider _configurationProvider;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private IReadOnlyDictionary<string, string?>? _environment;

    public GitSshEnvironmentHelper(ApplicationConfigurationProvider configurationProvider)
    {
        _configurationProvider = configurationProvider;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            _environment = await CreateEnvironmentAsync(cancellationToken);

            return _environment;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> CreateEnvironmentAsync(CancellationToken cancellationToken)
    {
        var repositoryParentDirectory = Directory.GetParent(
            _configurationProvider.GitWorkingDirectory)?.FullName
            ?? throw new InvalidOperationException("The Git working directory must have a parent directory.");
        var sshDirectory = Path.Combine(repositoryParentDirectory, ".ssh");
        var privateKeyPath = Path.Combine(sshDirectory, "gitea_sync_key");
        var knownHostsPath = Path.Combine(sshDirectory, "known_hosts");

        Directory.CreateDirectory(sshDirectory);
        SetPrivateDirectoryPermissions(sshDirectory);
        await WritePrivateKeyAsync(privateKeyPath, cancellationToken);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_SSH_COMMAND"] = string.Join(
                ' ',
                "ssh",
                "-i", QuoteForSshCommand(privateKeyPath),
                "-o", "IdentitiesOnly=yes",
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", $"UserKnownHostsFile={QuoteForSshCommand(knownHostsPath)}"),
            ["GIT_SSH_VARIANT"] = "ssh",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
    }

    private async Task WritePrivateKeyAsync(string privateKeyPath, CancellationToken cancellationToken)
    {
        var normalizedKey = _configurationProvider.GitSshPrivateKey
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n') + "\n";

        if (File.Exists(privateKeyPath)
            && string.Equals(
                await File.ReadAllTextAsync(privateKeyPath, cancellationToken),
                normalizedKey,
                StringComparison.Ordinal))
        {
            SetPrivateFilePermissions(privateKeyPath);
            return;
        }

        var temporaryPath = $"{privateKeyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                normalizedKey,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            SetPrivateFilePermissions(temporaryPath);
            File.Move(temporaryPath, privateKeyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string QuoteForSshCommand(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static void SetPrivateDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
