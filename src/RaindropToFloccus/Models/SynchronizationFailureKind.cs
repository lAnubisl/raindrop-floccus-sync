namespace RaindropToFloccus.Models;

public enum SynchronizationFailureKind
{
    Retryable,
    Deferred,
    RecoveryRequired,
    Fatal
}
