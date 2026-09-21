using RaindropToFloccus.Helpers;
using RaindropToFloccus.Models;
using RaindropToFloccus.Services;
using Xunit;

namespace RaindropToFloccus.Tests;

public sealed class GitPrioritySynchronizationPlannerTests
{
    [Fact]
    public void CreatePlan_preserves_source_sibling_order_when_an_item_is_deleted()
    {
        var folderId = new StableFolderId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var firstBookmarkId = new StableBookmarkId(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var deletedBookmarkId = new StableBookmarkId(Guid.Parse("20000000-0000-0000-0000-000000000002"));
        var folder = new BookmarkTreeFolder(folderId, null, "Folder");
        var firstBookmark = new BookmarkTreeBookmark(firstBookmarkId, null, "First", "https://example.com/first");
        var deletedBookmark = new BookmarkTreeBookmark(deletedBookmarkId, null, "Deleted", "https://example.com/deleted");
        var baseline = new BookmarkTree([folder], [firstBookmark, deletedBookmark]);
        var state = new SynchronizationState(
            1,
            1,
            true,
            baseline,
            baseline,
            [new FolderIdentityMapping(folderId, 1, 101)],
            [
                new BookmarkIdentityMapping(firstBookmarkId, 3, 103),
                new BookmarkIdentityMapping(deletedBookmarkId, 2, 102)
            ]);
        var xbel = new FloccusXbelSerializer();
        var source = xbel.Parse(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE xbel PUBLIC "+//IDN python.org//DTD XML Bookmark Exchange Language 1.0//EN//XML" "http://pyxml.sourceforge.net/topics/dtds/xbel.dtd">
            <xbel version="1.0">
            <!--- highestId :3: for Floccus bookmark sync browser extension -->
            <bookmark href="https://example.com/first" id="3">
              <title>First</title>
            </bookmark>
            <folder id="1">
              <title>Folder</title>
            </folder>
            <bookmark href="https://example.com/deleted" id="2">
              <title>Deleted</title>
            </bookmark>
            </xbel>
            """);
        var comparer = new ThreeWaySynchronizationComparer(new VersionedSynchronizationStateSerializer(), xbel);
        var comparison = comparer.Compare(
            state,
            source,
            [new RaindropCollection(101, null, folder.Title)],
            [new RaindropBookmark(103, -1, firstBookmark.Title, firstBookmark.Url)]);

        var plan = new GitPrioritySynchronizationPlanner(xbel).CreatePlan(comparison, source);

        var plannedDocument = xbel.Parse(plan.XbelContent);
        Assert.Equal([3L, 1L], plannedDocument.Items.Select(item => item.Id));
        Assert.Equal(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE xbel PUBLIC "+//IDN python.org//DTD XML Bookmark Exchange Language 1.0//EN//XML" "http://pyxml.sourceforge.net/topics/dtds/xbel.dtd">
            <xbel version="1.0">
            <!--- highestId :3: for Floccus bookmark sync browser extension -->
            <bookmark href="https://example.com/first" id="3">
              <title>First</title>
            </bookmark>
            <folder id="1">
              <title>Folder</title>
            </folder>
            </xbel>
            """.ReplaceLineEndings("\n"),
            plan.XbelContent);
    }
}
