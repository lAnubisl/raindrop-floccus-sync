using System.Globalization;
using RaindropToFloccus.Models;
using ApplicationConfigurationProvider = RaindropToFloccus.Interfaces.IConfigurationProvider;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Helpers;

public sealed class StandardOutputLoggingHelper : ApplicationLogger
{
    private readonly ApplicationConfigurationProvider _configurationProvider;
    private readonly Lock _outputLock = new();

    public StandardOutputLoggingHelper(ApplicationConfigurationProvider configurationProvider)
    {
        _configurationProvider = configurationProvider;
    }

    public void Info(string message)
    {
        if (_configurationProvider.LogLevel > ApplicationLogLevel.Information)
        {
            return;
        }

        Write(Console.Out, "Information", message);
    }

    public void Warning(string message)
    {
        if (_configurationProvider.LogLevel > ApplicationLogLevel.Warning)
        {
            return;
        }

        Write(Console.Out, "Warning", message);
    }

    public void Error(string message)
    {
        Write(Console.Error, "Error", message);
    }

    private void Write(TextWriter output, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var singleLineMessage = message.Replace('\r', ' ').Replace('\n', ' ');
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        lock (_outputLock)
        {
            output.WriteLine($"{timestamp} [{level}] {singleLineMessage}");
        }
    }
}
