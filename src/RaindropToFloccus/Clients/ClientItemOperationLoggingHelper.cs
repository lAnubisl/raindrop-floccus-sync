using System.Globalization;
using System.Text;
using RaindropToFloccus.Models;
using ApplicationLogger = RaindropToFloccus.Interfaces.ILogger;

namespace RaindropToFloccus.Clients;

internal static class ClientItemOperationLoggingHelper
{
    public static void Creating(ApplicationLogger logger, string itemType, string name, string system)
    {
        logger.Info($"Creating {itemType} \"{EscapeName(name)}\" in {system}.");
    }

    public static void Deleting(ApplicationLogger logger, string itemType, string name, string system)
    {
        logger.Info($"Deleting {itemType} \"{EscapeName(name)}\" from {system}.");
    }

    public static void LogFloccusChanges(
        ApplicationLogger logger, XbelDocument current, XbelDocument desired)
    {
        var currentFolders = new Dictionary<long, XbelFolder>();
        var currentBookmarks = new Dictionary<long, XbelBookmark>();
        var desiredFolders = new Dictionary<long, XbelFolder>();
        var desiredBookmarks = new Dictionary<long, XbelBookmark>();
        AddItems(current.Items, currentFolders, currentBookmarks);
        AddItems(desired.Items, desiredFolders, desiredBookmarks);

        foreach (var folder in desiredFolders.Values.Where(item => !currentFolders.ContainsKey(item.Id)))
        {
            Creating(logger, "folder", folder.Title, "Floccus");
        }
        foreach (var bookmark in desiredBookmarks.Values.Where(item => !currentBookmarks.ContainsKey(item.Id)))
        {
            Creating(logger, "bookmark", bookmark.Title, "Floccus");
        }
        foreach (var bookmark in currentBookmarks.Values.Where(item => !desiredBookmarks.ContainsKey(item.Id)))
        {
            Deleting(logger, "bookmark", bookmark.Title, "Floccus");
        }
        foreach (var folder in currentFolders.Values.Where(item => !desiredFolders.ContainsKey(item.Id)))
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

    private static void AddItems(IReadOnlyList<XbelItem> items,
        IDictionary<long, XbelFolder> folders, IDictionary<long, XbelBookmark> bookmarks)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case XbelFolder folder:
                    folders.Add(folder.Id, folder);
                    AddItems(folder.Children, folders, bookmarks);
                    break;
                case XbelBookmark bookmark:
                    bookmarks.Add(bookmark.Id, bookmark);
                    break;
                default:
                    throw new XbelFormatException("The XBEL document contains an unsupported item type.");
            }
        }
    }
}
