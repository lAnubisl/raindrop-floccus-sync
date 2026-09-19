namespace RaindropToFloccus.Models;

public sealed record SynchronizationJournalEnvelope(string Payload, string Sha256);
