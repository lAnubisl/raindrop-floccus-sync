using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using ApplicationConfigurationProvider = RaindropToFloccus.Interfaces.IConfigurationProvider;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Clients;

public sealed class GiteaSshGitClient : IGitRepositoryClient
{
    private const string BranchName = "main";
    private static readonly string[] SynchronizationFiles =
    [
        "bookmarks.xbel",
        ".raindrop-sync/state.json"
    ];

    private readonly ApplicationConfigurationProvider _configurationProvider;
    private readonly ICommandRunner _commandRunner;
    private readonly IGitSshEnvironmentProvider _sshEnvironmentProvider;
    private readonly ApplicationLogger _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _initialized;

    public GiteaSshGitClient(
        ApplicationConfigurationProvider configurationProvider,
        ICommandRunner commandRunner,
        IGitSshEnvironmentProvider sshEnvironmentProvider,
        ApplicationLogger logger)
    {
        _configurationProvider = configurationProvider;
        _commandRunner = commandRunner;
        _sshEnvironmentProvider = sshEnvironmentProvider;
        _logger = logger;
    }

    public string WorkingDirectory => _configurationProvider.GitWorkingDirectory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            _logger.Info("Fetching the latest Git repository state.");
            await RunGitAsync(
                "fetch",
                ["fetch", "--prune", "origin", $"+refs/heads/{BranchName}:refs/remotes/origin/{BranchName}"],
                cancellationToken);
            var merge = await RunGitAllowingExitCodesAsync(
                "fast-forward main",
                ["merge", "--ff-only", $"origin/{BranchName}"],
                [0, 1],
                cancellationToken);
            if (merge.ExitCode == 0)
            {
                return true;
            }

            var status = await RunGitAsync("inspect working tree after failed Git refresh", ["status", "--porcelain"], cancellationToken);
            if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            {
                _logger.Warning("Git refresh could not update a working tree with pending local synchronization files.");
                return false;
            }

            var localIsAncestor = await RunGitAllowingExitCodesAsync(
                "check whether local main is behind origin",
                ["merge-base", "--is-ancestor", "HEAD", $"origin/{BranchName}"],
                [0, 1],
                cancellationToken);
            if (localIsAncestor.ExitCode == 1)
            {
                _logger.Warning("Git main diverged from origin/main while synchronization changes were pending.");
                return false;
            }

            throw new GitClientException("fast-forward main", merge.ExitCode, merge.StandardError);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            var result = await RunGitAsync("read HEAD", ["rev-parse", "HEAD"], cancellationToken);
            return result.StandardOutput.Trim();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> CommitSynchronizationFilesAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Contains('\r', StringComparison.Ordinal)
            || message.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("The Git commit message must contain one line.", nameof(message));
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            await RunGitAsync(
                "stage synchronization files",
                ["add", "--", .. SynchronizationFiles],
                cancellationToken);

            var difference = await RunGitAllowingExitCodesAsync(
                "inspect staged changes",
                ["diff", "--cached", "--quiet", "--exit-code", "--", .. SynchronizationFiles],
                [0, 1],
                cancellationToken);
            if (difference.ExitCode == 0)
            {
                return false;
            }

            await RunGitAsync(
                "commit synchronization files",
                ["commit", "--only", "-m", message, "--", .. SynchronizationFiles],
                cancellationToken);
            _logger.Info("Committed synchronization changes to the local Git repository.");
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            _logger.Info("Pushing synchronization changes to Gitea.");
            await RunGitAsync(
                "push main",
                ["push", "origin", $"{BranchName}:{BranchName}"],
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> PushSynchronizationFilesIfRemoteUnchangedAsync(
        string expectedRemoteRevision,
        XbelDocument previousXbel,
        XbelDocument currentXbel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRemoteRevision);
        ArgumentNullException.ThrowIfNull(previousXbel);
        ArgumentNullException.ThrowIfNull(currentXbel);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            _logger.Info("Pushing synchronization changes with a Git revision lease.");
            var push = await RunGitAllowingExitCodesAsync(
                "push synchronization files with revision lease",
                ["push", "--porcelain", $"--force-with-lease=refs/heads/{BranchName}:{expectedRemoteRevision}",
                    "origin", $"{BranchName}:{BranchName}"],
                [0, 1],
                cancellationToken);
            if (push.ExitCode == 0)
            {
                LogXbelChanges(previousXbel, currentXbel);
                return true;
            }

            var remoteRevision = await GetRemoteRevisionAsync(cancellationToken);
            if (!string.Equals(remoteRevision, expectedRemoteRevision, StringComparison.Ordinal))
            {
                _logger.Warning("Git rejected the synchronization push because Floccus advanced main.");
                return false;
            }

            throw new GitClientException("push synchronization files with revision lease", push.ExitCode,
                string.Concat(push.StandardError, Environment.NewLine, push.StandardOutput));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestoreRemoteAfterRejectedSynchronizationPushAsync(
        string expectedRemoteRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRemoteRevision);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            await RunGitAsync(
                "fetch remote after rejected synchronization push",
                ["fetch", "--prune", "origin", $"+refs/heads/{BranchName}:refs/remotes/origin/{BranchName}"],
                cancellationToken);
            var remoteRevision = await GetRemoteRevisionAsync(cancellationToken);
            if (string.Equals(remoteRevision, expectedRemoteRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Git remote did not advance after a rejected synchronization push.");
            }

            var localRevision = await GetCurrentRevisionCoreAsync(cancellationToken);
            if (string.Equals(localRevision, expectedRemoteRevision, StringComparison.Ordinal))
            {
                await RunGitAsync("fast-forward main after rejected synchronization push",
                    ["merge", "--ff-only", $"origin/{BranchName}"], cancellationToken);
                return;
            }

            var parent = await RunGitAsync("read rejected synchronization commit parent", ["rev-parse", "HEAD^"], cancellationToken);
            if (!string.Equals(parent.StandardOutput.Trim(), expectedRemoteRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The local branch contains commits that cannot be safely replaced after a rejected synchronization push.");
            }

            var changedFiles = await RunGitAsync("inspect rejected synchronization commit",
                ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"], cancellationToken);
            var changedFileSet = changedFiles.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (changedFileSet.Count == 0 || !changedFileSet.IsSubsetOf(SynchronizationFiles))
            {
                throw new InvalidOperationException(
                    "The rejected local commit changes files outside the synchronization contract.");
            }

            var status = await RunGitAsync("inspect working tree before Git reconciliation", ["status", "--porcelain"], cancellationToken);
            if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            {
                throw new InvalidOperationException(
                    "The working tree changed while reconciling a rejected synchronization push.");
            }

            await RunGitAsync("preserve rejected synchronization commit",
                ["update-ref", $"refs/raindrop-sync/rejected/{localRevision}", localRevision], cancellationToken);
            await RunGitAsync("restore main to current remote after rejected synchronization push",
                ["reset", "--merge", $"origin/{BranchName}"], cancellationToken);
            _logger.Warning("Preserved the rejected local synchronization commit and restored current origin/main for reconciliation.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> IsSynchronizationCommitAtHeadAsync(
        string expectedParentRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedParentRevision);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            var localRevision = await GetCurrentRevisionCoreAsync(cancellationToken);
            if (string.Equals(localRevision, expectedParentRevision, StringComparison.Ordinal))
            {
                return false;
            }

            var parent = await RunGitAsync("read synchronization commit parent", ["rev-parse", "HEAD^"], cancellationToken);
            if (!string.Equals(parent.StandardOutput.Trim(), expectedParentRevision, StringComparison.Ordinal))
            {
                return false;
            }

            var changedFiles = await RunGitAsync("inspect synchronization commit",
                ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"], cancellationToken);
            var changedFileSet = changedFiles.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            return changedFileSet.Count > 0 && changedFileSet.IsSubsetOf(SynchronizationFiles);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var sshEnvironment = await _sshEnvironmentProvider.GetEnvironmentAsync(cancellationToken);
        var gitDirectory = Path.Combine(WorkingDirectory, ".git");

        if (!Directory.Exists(gitDirectory))
        {
            await CloneRepositoryAsync(sshEnvironment, cancellationToken);
        }
        else
        {
            await ValidateAndCheckoutRepositoryAsync(cancellationToken);
        }

        await ConfigureAuthorAsync(cancellationToken);

        _initialized = true;
    }

    private async Task CloneRepositoryAsync(IReadOnlyDictionary<string, string?> sshEnvironment,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(WorkingDirectory)
            && Directory.EnumerateFileSystemEntries(WorkingDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Git working directory '{WorkingDirectory}' is not empty and is not a Git repository.");
        }

        Directory.CreateDirectory(Directory.GetParent(WorkingDirectory)?.FullName
            ?? throw new InvalidOperationException("The Git working directory must have a parent directory."));
        _logger.Info("Cloning the Gitea repository over SSH.");
        await RunCommandAsync(
            "clone repository",
            [
                "clone",
                "--branch", BranchName,
                "--single-branch",
                "--origin", "origin",
                _configurationProvider.GitRepositoryUrl,
                WorkingDirectory
            ],
            workingDirectory: null,
            sshEnvironment,
            [0],
            cancellationToken);
    }

    private async Task ValidateAndCheckoutRepositoryAsync(CancellationToken cancellationToken)
    {
        var remote = await RunGitAsync("read origin URL", ["remote", "get-url", "origin"], cancellationToken);
        if (!string.Equals(
                remote.StandardOutput.Trim(),
                _configurationProvider.GitRepositoryUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The existing Git working directory belongs to a different origin repository.");
        }

        await RunGitAsync("checkout main", ["checkout", BranchName], cancellationToken);
    }

    private async Task ConfigureAuthorAsync(CancellationToken cancellationToken)
    {
        await RunGitAsync(
            "configure author name",
            ["config", "--local", "user.name", _configurationProvider.GitAuthorName],
            cancellationToken);
        await RunGitAsync(
            "configure author email",
            ["config", "--local", "user.email", _configurationProvider.GitAuthorEmail],
            cancellationToken);
    }

    private async Task<string> GetRemoteRevisionAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync("read origin/main", ["rev-parse", $"origin/{BranchName}"], cancellationToken);
        return result.StandardOutput.Trim();
    }

    private async Task<string> GetCurrentRevisionCoreAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync("read HEAD", ["rev-parse", "HEAD"], cancellationToken);
        return result.StandardOutput.Trim();
    }

    private void LogXbelChanges(XbelDocument previous, XbelDocument current)
    {
        var previousItems = Flatten(previous);
        var currentItems = Flatten(current);

        foreach (var item in previousItems)
        {
            if (!currentItems.TryGetValue(item.Key, out var currentItem)
                || item.Value.IsFolder != currentItem.IsFolder)
            {
                LogChange("Removing", item.Value, "from");
            }
        }

        foreach (var item in currentItems)
        {
            if (!previousItems.TryGetValue(item.Key, out var previousItem)
                || item.Value.IsFolder != previousItem.IsFolder)
            {
                LogChange("Adding", item.Value, "to");
            }
            else if (item.Value != previousItem)
            {
                LogChange("Changing", item.Value, "in");
            }
        }
    }

    private void LogChange(string action, XbelLogItem item, string preposition)
    {
        var type = item.IsFolder ? "folder" : "bookmark";
        _logger.Info($"{action} {type} \"{item.Title}\" {preposition} Gitea.");
    }

    private static Dictionary<long, XbelLogItem> Flatten(XbelDocument document)
    {
        var result = new Dictionary<long, XbelLogItem>();
        AddItems(document.Items, null, result);
        return result;
    }

    private static void AddItems(IEnumerable<XbelItem> items, long? parentId,
        Dictionary<long, XbelLogItem> result)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case XbelFolder folder:
                    result.Add(folder.Id, new XbelLogItem(true, parentId, folder.Title, null));
                    AddItems(folder.Children, folder.Id, result);
                    break;
                case XbelBookmark bookmark:
                    result.Add(bookmark.Id, new XbelLogItem(false, parentId, bookmark.Title, bookmark.Url));
                    break;
            }
        }
    }

    private Task<CommandResult> RunGitAsync(
        string operation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return RunGitAllowingExitCodesAsync(operation, arguments, [0], cancellationToken);
    }

    private async Task<CommandResult> RunGitAllowingExitCodesAsync(
        string operation,
        IReadOnlyList<string> arguments,
        IReadOnlyList<int> allowedExitCodes,
        CancellationToken cancellationToken)
    {
        var sshEnvironment = await _sshEnvironmentProvider.GetEnvironmentAsync(cancellationToken);
        return await RunCommandAsync(
            operation,
            arguments,
            WorkingDirectory,
            sshEnvironment,
            allowedExitCodes,
            cancellationToken);
    }

    private async Task<CommandResult> RunCommandAsync(
        string operation,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<int> allowedExitCodes,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandSpec("git", arguments, workingDirectory, environment),
            cancellationToken);
        if (!allowedExitCodes.Contains(result.ExitCode))
        {
            throw new GitClientException(operation, result.ExitCode, result.StandardError);
        }

        return result;
    }

    private sealed record XbelLogItem(bool IsFolder, long? ParentId, string Title, string? Url);
}
