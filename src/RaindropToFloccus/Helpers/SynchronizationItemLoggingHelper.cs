using System.Globalization;
using System.Text;
using RaindropToFloccus.Models;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Helpers;

internal static class SynchronizationItemLoggingHelper
{
    public static void Creating(ApplicationLogger logger, string itemType, string name, string system)
    {
        logger.Info($"Creating {itemType} \"{EscapeName(name)}\" in {system}.");
    }

    public static void Deleting(ApplicationLogger logger, string itemType, string name, string system)
    {
        logger.Info($"Deleting {itemType} \"{EscapeName(name)}\" from {system}.");
    }

    public static void LogFloccusCreatesAndDeletes(
        ApplicationLogger logger, XbelDocument source, SynchronizationPlan plan)
    {
        var sourceFolders = new Dictionary<long, XbelFolder>();
        var sourceBookmarks = new Dictionary<long, XbelBookmark>();
        AddSourceItems(source.Items, sourceFolders, sourceBookmarks);

        var plannedFolderIds = plan.Folders.ToDictionary(item => item.StableId, item => item.XbelId);
        var plannedBookmarkIds = plan.Bookmarks.ToDictionary(item => item.StableId, item => item.XbelId);

        foreach (var folder in plan.XbelTree.Folders
                     .Where(item => !sourceFolders.ContainsKey(plannedFolderIds[item.Id])))
        {
            Creating(logger, "folder", folder.Title, "Floccus");
        }
        foreach (var bookmark in plan.XbelTree.Bookmarks
                     .Where(item => !sourceBookmarks.ContainsKey(plannedBookmarkIds[item.Id])))
        {
            Creating(logger, "bookmark", bookmark.Title, "Floccus");
        }

        var retainedFolderIds = plannedFolderIds.Values.ToHashSet();
        var retainedBookmarkIds = plannedBookmarkIds.Values.ToHashSet();
        foreach (var bookmark in sourceBookmarks.Values.Where(item => !retainedBookmarkIds.Contains(item.Id)))
        {
            Deleting(logger, "bookmark", bookmark.Title, "Floccus");
        }
        foreach (var folder in sourceFolders.Values.Where(item => !retainedFolderIds.Contains(item.Id)))
        {
            Deleting(logger, "folder", folder.Title, "Floccus");
        }
    }

    private static string EscapeName(string name)
    {
        var escaped = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("\\u");
                        escaped.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }
                    break;
            }
        }

        return escaped.ToString();
    }

    private static void AddSourceItems(IReadOnlyList<XbelItem> items,
        IDictionary<long, XbelFolder> folders, IDictionary<long, XbelBookmark> bookmarks)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case XbelFolder folder:
                    folders.Add(folder.Id, folder);
                    AddSourceItems(folder.Children, folders, bookmarks);
                    break;
                case XbelBookmark bookmark:
                    bookmarks.Add(bookmark.Id, bookmark);
                    break;
                default:
                    throw new XbelFormatException("The source XBEL contains an unsupported item type.");
            }
        }
    }
}
