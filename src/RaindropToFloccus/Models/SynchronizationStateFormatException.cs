namespace RaindropToFloccus.Models;

public sealed class SynchronizationStateFormatException : Exception
{
    public SynchronizationStateFormatException(string message)
        : base(message)
    {
    }

    public SynchronizationStateFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
