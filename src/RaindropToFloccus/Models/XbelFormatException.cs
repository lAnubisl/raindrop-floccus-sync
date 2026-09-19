namespace RaindropToFloccus.Models;

public sealed class XbelFormatException : Exception
{
    public XbelFormatException(string message)
        : base(message)
    {
    }

    public XbelFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
