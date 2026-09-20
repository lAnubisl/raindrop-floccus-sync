using RaindropToFloccus.Clients;
using RaindropToFloccus.Helpers;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.Tests;

public sealed class GiteaSshGitClientLoggingTests
{
    [Fact]
    public async Task Floccus_item_logs_are_written_immediately_before_the_remote_push()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(), $"raindrop-floccus-git-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".git"));
        try
        {
            var logger = new RecordingLogger();
            var serializer = new FloccusXbelSerializer();
            var current = serializer.Serialize(new XbelDocument(2,
            [
                new XbelFolder(1, "Old Folder",
                [
                    new XbelBookmark(2, "Old Bookmark", "https://old.example")
                ])
            ]));
            var desired = serializer.Serialize(new XbelDocument(4,
            [
                new XbelFolder(3, "New Folder",
                [
                    new XbelBookmark(4, "New Bookmark", "https://new.example")
                ])
            ]));
            var runner = new RecordingCommandRunner(current, desired, logger);
            var configuration = new TestConfiguration(workingDirectory);
            var client = new GiteaSshGitClient(
                configuration, runner, new TestSshEnvironmentProvider(), serializer, logger);

            var pushed = await client.PushSynchronizationFilesIfRemoteUnchangedAsync("base-revision");

            Assert.True(pushed);
            Assert.True(runner.PushObserved);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class RecordingCommandRunner(
        string currentXbel, string desiredXbel, RecordingLogger logger) : ICommandRunner
    {
        private static readonly string[] ExpectedItemMessages =
        [
            "Creating folder \"New Folder\" in Floccus.",
            "Creating bookmark \"New Bookmark\" in Floccus.",
            "Deleting bookmark \"Old Bookmark\" from Floccus.",
            "Deleting folder \"Old Folder\" from Floccus."
        ];

        public bool PushObserved { get; private set; }

        public Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = spec.Arguments;
            if (arguments.SequenceEqual(["remote", "get-url", "origin"]))
            {
                return Success("ssh://git@example.test/repository.git\n");
            }
            if (arguments.Count == 2 && arguments[0] == "show")
            {
                return Success(arguments[1] == "HEAD:bookmarks.xbel" ? desiredXbel : currentXbel);
            }
            if (arguments.Count > 0 && arguments[0] == "push")
            {
                Assert.Equal(ExpectedItemMessages, logger.Messages.TakeLast(ExpectedItemMessages.Length));
                PushObserved = true;
            }

            return Success();
        }

        private static Task<CommandResult> Success(string output = "") =>
            Task.FromResult(new CommandResult(0, output, ""));
    }

    private sealed class TestSshEnvironmentProvider : IGitSshEnvironmentProvider
    {
        public Task<IReadOnlyDictionary<string, string?>> GetEnvironmentAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(
                new Dictionary<string, string?>());
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warning(string message)
        {
        }

        public void Error(string message)
        {
        }
    }

    private sealed class TestConfiguration(string workingDirectory) : IConfigurationProvider
    {
        public string RaindropApiToken => throw new NotSupportedException();
        public TimeSpan RaindropRequestTimeout => throw new NotSupportedException();
        public string GitRepositoryUrl => "ssh://git@example.test/repository.git";
        public string GitSshPrivateKey => "unused";
        public string GitAuthorName => "Test";
        public string GitAuthorEmail => "test@example.test";
        public string GitWorkingDirectory => workingDirectory;
        public TimeSpan SynchronizationInterval => throw new NotSupportedException();
        public TimeSpan RetryInterval => throw new NotSupportedException();
        public int MaximumRetryAttempts => throw new NotSupportedException();
        public ApplicationLogLevel LogLevel => ApplicationLogLevel.Information;
        public int HealthCheckPort => throw new NotSupportedException();
    }
}
