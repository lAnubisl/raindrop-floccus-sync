namespace RaindropToFloccus.Models;

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
