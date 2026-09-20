using RaindropToFloccus.Clients;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.Tests;

public sealed class ClientItemOperationLoggingHelperTests
{
    [Fact]
    public void Floccus_changes_report_each_created_and_deleted_item()
    {
        var logger = new RecordingLogger();
        var current = new XbelDocument(2,
        [
            new XbelFolder(1, "Old Folder",
            [
                new XbelBookmark(2, "Old Bookmark", "https://old.example")
            ])
        ]);
        var desired = new XbelDocument(4,
        [
            new XbelFolder(3, "New Folder",
            [
                new XbelBookmark(4, "New Bookmark", "https://new.example")
            ])
        ]);

        ClientItemOperationLoggingHelper.LogFloccusChanges(logger, current, desired);

        Assert.Equal(
        [
            "Creating folder \"New Folder\" in Floccus.",
            "Creating bookmark \"New Bookmark\" in Floccus.",
            "Deleting bookmark \"Old Bookmark\" from Floccus.",
            "Deleting folder \"Old Folder\" from Floccus."
        ], logger.Messages);
    }

    [Fact]
    public void Item_names_are_escaped_as_single_line_values()
    {
        var logger = new RecordingLogger();

        ClientItemOperationLoggingHelper.Creating(
            logger, "bookmark", "My \"Bookmark\"\nName", "Floccus");

        Assert.Equal(
            "Creating bookmark \"My \\\"Bookmark\\\"\\nName\" in Floccus.",
            Assert.Single(logger.Messages));
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
}
