using RaindropToFloccus.Helpers;
using RaindropToFloccus.Models;
using Xunit;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Tests;

public sealed class SynchronizationItemLoggingHelperTests
{
    [Fact]
    public void Creating_includes_operation_type_escaped_name_and_system()
    {
        var logger = new RecordingLogger();

        SynchronizationItemLoggingHelper.Creating(
            logger, "bookmark", "My \"Bookmark\"\nName", "Floccus");

        Assert.Equal(
            "Creating bookmark \"My \\\"Bookmark\\\"\\nName\" in Floccus.",
            Assert.Single(logger.Messages));
    }

    [Fact]
    public void Deleting_includes_operation_type_name_and_system()
    {
        var logger = new RecordingLogger();

        SynchronizationItemLoggingHelper.Deleting(logger, "folder", "My Folder Name", "Raindrop");

        Assert.Equal(
            "Deleting folder \"My Folder Name\" from Raindrop.",
            Assert.Single(logger.Messages));
    }

    [Fact]
    public void Floccus_changes_report_each_created_and_deleted_item()
    {
        var logger = new RecordingLogger();
        var folderId = new StableFolderId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var bookmarkId = new StableBookmarkId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var source = new XbelDocument(2,
        [
            new XbelFolder(1, "Old Folder",
            [
                new XbelBookmark(2, "Old Bookmark", "https://old.example")
            ])
        ]);
        var desiredTree = new BookmarkTree(
            [new BookmarkTreeFolder(folderId, null, "New Folder")],
            [new BookmarkTreeBookmark(bookmarkId, folderId, "New Bookmark", "https://new.example")]);
        var plan = new SynchronizationPlan(
            desiredTree,
            desiredTree,
            [new PendingFolderMapping(folderId, 3, 10)],
            [new PendingBookmarkMapping(bookmarkId, 4, 20)],
            "planned XBEL");

        SynchronizationItemLoggingHelper.LogFloccusCreatesAndDeletes(logger, source, plan);

        Assert.Equal(
        [
            "Creating folder \"New Folder\" in Floccus.",
            "Creating bookmark \"New Bookmark\" in Floccus.",
            "Deleting bookmark \"Old Bookmark\" from Floccus.",
            "Deleting folder \"Old Folder\" from Floccus."
        ], logger.Messages);
    }

    private sealed class RecordingLogger : ApplicationLogger
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
