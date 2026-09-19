namespace RaindropToFloccus.Interfaces;

public interface IGitSshEnvironmentProvider
{
    Task<IReadOnlyDictionary<string, string?>> GetEnvironmentAsync(
        CancellationToken cancellationToken = default);
}
