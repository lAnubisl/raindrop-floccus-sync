namespace RaindropToFloccus.Models;

public sealed class SynchronizationRecoveryRequiredException : Exception
{
    public SynchronizationRecoveryRequiredException(string message) : base(message) { }
    public SynchronizationRecoveryRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}
