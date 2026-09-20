using RaindropToFloccus.Interfaces;

namespace RaindropToFloccus.IntegrationTests;

public sealed class TestLogger : ILogger
{
    public List<string> InformationMessages { get; } = [];
    public List<string> WarningMessages { get; } = [];
    public List<string> ErrorMessages { get; } = [];

    public void Info(string message) => InformationMessages.Add(message);

    public void Warning(string message) => WarningMessages.Add(message);

    public void Error(string message) => ErrorMessages.Add(message);
}
