using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.Helpers;

public sealed class LocalSynchronizationJournalStore : ISynchronizationJournalStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly IGitRepositoryClient _git;

    public LocalSynchronizationJournalStore(IGitRepositoryClient git) => _git = git;

    // Stored in the persistent clone, but never staged, pushed, or exposed to Floccus.
    private string JournalPath => Path.Combine(_git.WorkingDirectory, ".git", "raindrop-sync-pending.json");

    public async Task<SynchronizationJournal?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(JournalPath))
            throw new SynchronizationRecoveryRequiredException("The synchronization journal path is a directory.");
        if (!File.Exists(JournalPath)) return null;
        try
        {
            if (new FileInfo(JournalPath).Length > 128 * 1024 * 1024)
                throw new JsonException("Journal size limit exceeded.");
            var content = await File.ReadAllTextAsync(JournalPath, cancellationToken);
            var envelope = JsonSerializer.Deserialize<SynchronizationJournalEnvelope>(content, Options)
                ?? throw new JsonException("Missing journal envelope.");
            if (envelope.Payload is null || Hash(envelope.Payload) != envelope.Sha256)
                throw new JsonException("Journal checksum mismatch.");
            var journal = JsonSerializer.Deserialize<SynchronizationJournal>(envelope.Payload, Options)
                ?? throw new JsonException("Missing journal payload.");
            if (journal.Version != 1) throw new JsonException("Unsupported journal version.");
            return journal;
        }
        catch (JsonException exception)
        {
            throw new SynchronizationRecoveryRequiredException(
                "The local synchronization journal is damaged or unsupported. Preserve the working volume and inspect it before recovery.",
                exception);
        }
    }

    public async Task SaveAsync(SynchronizationJournal journal, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(journal, Options);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new SynchronizationJournalEnvelope(payload, Hash(payload)), Options));
        var path = JournalPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
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

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(JournalPath);
        return Task.CompletedTask;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            MaxDepth = 144
        };
        options.Converters.Add(new StableFolderIdJsonConverter());
        options.Converters.Add(new StableBookmarkIdJsonConverter());
        return options;
    }
}
