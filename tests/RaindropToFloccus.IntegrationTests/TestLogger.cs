using RaindropToFloccus.Interfaces;

namespace RaindropToFloccus.IntegrationTests;

public sealed class TestLogger : ILogger
{
    public List<string> Information { get; } = [];

    public void Info(string message) => Information.Add(message);

    public void Warning(string message)
    {
    }

    public void Error(string message)
    {
    }
}
