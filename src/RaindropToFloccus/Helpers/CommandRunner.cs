using System.Diagnostics;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.Helpers;

public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        CommandSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.FileName);

        var startInfo = CreateStartInfo(spec);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            return new CommandResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(CommandSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment is not null)
        {
            foreach (var variable in spec.Environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }
        return startInfo;
    }

}
