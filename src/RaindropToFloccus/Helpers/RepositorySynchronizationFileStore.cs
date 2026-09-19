using System.Text;
using RaindropToFloccus.Interfaces;

namespace RaindropToFloccus.Helpers;

public sealed class RepositorySynchronizationFileStore : ISynchronizationFileStore
{
    private const string XbelFileName = "bookmarks.xbel";
    private const string StateDirectoryName = ".raindrop-sync";
    private const string StateFileName = "state.json";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IGitRepositoryClient _gitRepositoryClient;

    public RepositorySynchronizationFileStore(IGitRepositoryClient gitRepositoryClient)
    {
        _gitRepositoryClient = gitRepositoryClient;
    }

    public async Task<string> ReadXbelAsync(CancellationToken cancellationToken = default)
    {
        var path = GetXbelPath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required XBEL file '{XbelFileName}' does not exist in the Git repository.",
                path);
        }

        return await File.ReadAllTextAsync(path, Utf8WithoutBom, cancellationToken);
    }

    public async Task<string?> ReadStateIfExistsAsync(CancellationToken cancellationToken = default)
    {
        var path = GetStatePath();
        if (Directory.Exists(path))
        {
            throw new IOException(
                $"Synchronization state path '{StateDirectoryName}/{StateFileName}' is a directory.");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, Utf8WithoutBom, cancellationToken);
    }

    public async Task WriteNewStateAtomicallyAsync(
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var destinationPath = GetStatePath();
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException(
                $"Synchronization state path '{StateDirectoryName}/{StateFileName}' already exists.");
        }

        var stateDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The synchronization state path has no parent directory.");
        Directory.CreateDirectory(stateDirectory);

        var temporaryPath = Path.Combine(
            stateDirectory,
            $".{StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Utf8WithoutBom, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task WriteXbelAtomicallyAsync(string content, CancellationToken cancellationToken = default) =>
        ReplaceAtomicallyAsync(GetXbelPath(), content, cancellationToken);

    public Task WriteStateAtomicallyAsync(string content, CancellationToken cancellationToken = default) =>
        ReplaceAtomicallyAsync(GetStatePath(), content, cancellationToken);

    private static async Task ReplaceAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!File.Exists(path)) throw new FileNotFoundException("The synchronization file to replace is missing.", path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Utf8WithoutBom.GetBytes(content), cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string GetXbelPath() =>
        Path.Combine(_gitRepositoryClient.WorkingDirectory, XbelFileName);

    private string GetStatePath() =>
        Path.Combine(_gitRepositoryClient.WorkingDirectory, StateDirectoryName, StateFileName);
}
