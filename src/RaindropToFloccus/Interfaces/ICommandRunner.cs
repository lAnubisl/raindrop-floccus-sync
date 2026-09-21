namespace RaindropToFloccus.Interfaces;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        CommandSpec spec,
        CancellationToken cancellationToken = default);
}
