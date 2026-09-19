using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationJournalStore
{
    Task<SynchronizationJournal?> ReadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SynchronizationJournal journal, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
