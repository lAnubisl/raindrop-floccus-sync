namespace RaindropToFloccus.Models;

public sealed class GitClientException : Exception
{
    public GitClientException(string operation, int exitCode, string error)
        : base(CreateMessage(operation, exitCode, error))
    {
        Operation = operation;
        ExitCode = exitCode;
        Error = error;
    }

    public string Operation { get; }

    public int ExitCode { get; }

    public string Error { get; }

    private static string CreateMessage(string operation, int exitCode, string error)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? "Git returned no error details." : error.Trim();
        return $"Git operation '{operation}' failed with exit code {exitCode}: {detail}";
    }
}
