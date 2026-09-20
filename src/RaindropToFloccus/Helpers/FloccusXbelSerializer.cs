using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.Helpers;

public sealed partial class FloccusXbelSerializer : IXbelDocumentSerializer
{
    private const string XbelVersion = "1.0";
    private const string XbelPublicIdentifier =
        "+//IDN python.org//DTD XML Bookmark Exchange Language 1.0//EN//XML";
    private const string XbelSystemIdentifier = "http://pyxml.sourceforge.net/topics/dtds/xbel.dtd";
    private const string HighestIdCommentSuffix = "for Floccus bookmark sync browser extension";
    private const string FloccusXmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
    private const long MaximumFloccusId = 9_007_199_254_740_991;
    private const int MaximumFolderDepth = 128;

    public XbelDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new XbelFormatException("The XBEL document is empty.");
        }

        var xmlDocument = ReadXmlDocument(content);
        var root = ValidateDocumentStructure(xmlDocument);
        var highestIdComment = ReadHighestIdComment(xmlDocument, root);
        var highestId = ParseHighestId(highestIdComment);
        var identifiers = new HashSet<long>();
        var items = ParseItems(root, highestIdComment, identifiers, depth: 0);
        ValidateHighestId(highestId, identifiers);
        return new XbelDocument(highestId, items);
    }

    public string Serialize(XbelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);

        var root = new XElement(
            "xbel",
            new XAttribute("version", XbelVersion),
            new XComment(
                $"- highestId :{document.HighestId.ToString(CultureInfo.InvariantCulture)}: "
                + HighestIdCommentSuffix));

        foreach (var item in document.Items)
        {
            root.Add(SerializeItem(item));
        }

        var xmlDocument = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "xbel",
                XbelPublicIdentifier,
                XbelSystemIdentifier,
                internalSubset: null),
            root);

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = true
        };

        using (var writer = XmlWriter.Create(stream, settings))
        {
            xmlDocument.Save(writer);
        }

        return FloccusXmlDeclaration + "\n" + Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XDocument ReadXmlDocument(string content)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                MaxCharactersFromEntities = 1_024,
                MaxCharactersInDocument = 16 * 1_024 * 1_024
            };

            using var stringReader = new StringReader(content);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            return XDocument.Load(
                xmlReader,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new XbelFormatException(
                $"The XBEL document is not well-formed XML at line {exception.LineNumber}, "
                + $"position {exception.LinePosition}.",
                exception);
        }
    }

    private static XElement ValidateDocumentStructure(XDocument xmlDocument)
    {
        ValidateDeclaration(xmlDocument.Declaration);
        ValidateDocumentLevelNodes(xmlDocument);
        ValidateDocumentType(xmlDocument.DocumentType);

        var root = xmlDocument.Root
            ?? throw new XbelFormatException("The XBEL document does not contain a root element.");
        ValidateElementName(root, "xbel");
        ValidateAttributes(root, ["version"]);
        if (!string.Equals(root.Attribute("version")?.Value, XbelVersion, StringComparison.Ordinal))
        {
            throw CreateFormatException(root, "The root xbel element must have version=\"1.0\".");
        }
        return root;
    }

    private static XComment ReadHighestIdComment(XDocument xmlDocument, XElement root)
    {
        var comments = xmlDocument.DescendantNodes().OfType<XComment>().ToList();
        if (comments.Count != 1 || comments[0].Parent != root)
        {
            throw new XbelFormatException(
                "The XBEL document must contain exactly one Floccus highestId comment directly under xbel.");
        }
        return comments[0];
    }

    private static void ValidateDeclaration(XDeclaration? declaration)
    {
        if (declaration is null
            || !string.Equals(declaration.Version, "1.0", StringComparison.Ordinal)
            || !string.Equals(declaration.Encoding, "UTF-8", StringComparison.OrdinalIgnoreCase)
            || declaration.Standalone is not null)
        {
            throw new XbelFormatException(
                "The XBEL document must start with the XML 1.0 declaration using UTF-8 encoding.");
        }
    }

    private static void ValidateDocumentLevelNodes(XDocument document)
    {
        foreach (var node in document.Nodes())
        {
            if (node is XElement or XDocumentType || IsWhitespace(node))
            {
                continue;
            }

            throw CreateFormatException(node, "The XBEL document contains an unsupported top-level node.");
        }
    }

    private static void ValidateDocumentType(XDocumentType? documentType)
    {
        if (documentType is null
            || !string.Equals(documentType.Name, "xbel", StringComparison.Ordinal)
            || !string.Equals(documentType.PublicId, XbelPublicIdentifier, StringComparison.Ordinal)
            || !string.Equals(documentType.SystemId, XbelSystemIdentifier, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(documentType.InternalSubset))
        {
            throw new XbelFormatException(
                "The XBEL document must use the standard XBEL 1.0 document type without an internal subset.");
        }
    }

    private static long ParseHighestId(XComment comment)
    {
        var match = HighestIdCommentRegex().Match(comment.Value);
        if (!match.Success
            || !long.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var highestId)
            || highestId < 0
            || highestId > MaximumFloccusId)
        {
            throw CreateFormatException(comment, "The Floccus highestId comment is invalid.");
        }

        return highestId;
    }

    private static IReadOnlyList<XbelItem> ParseItems(
        XElement parent,
        XComment? allowedComment,
        ISet<long> identifiers,
        int depth)
    {
        var items = new List<XbelItem>();
        foreach (var node in parent.Nodes())
        {
            if (IsWhitespace(node) || ReferenceEquals(node, allowedComment))
            {
                continue;
            }

            if (node is not XElement element)
            {
                throw CreateFormatException(node, "An XBEL container contains an unsupported node.");
            }

            items.Add(element.Name.LocalName switch
            {
                "folder" when element.Name.Namespace == XNamespace.None =>
                    ParseFolder(element, identifiers, depth + 1),
                "bookmark" when element.Name.Namespace == XNamespace.None => ParseBookmark(element, identifiers),
                _ => throw CreateFormatException(
                    element,
                    $"Unsupported XBEL element '{element.Name}'.")
            });
        }

        return items;
    }

    private static XbelFolder ParseFolder(XElement element, ISet<long> identifiers, int depth)
    {
        if (depth > MaximumFolderDepth)
        {
            throw CreateFormatException(
                element,
                $"XBEL folder nesting exceeds the supported depth of {MaximumFolderDepth}.");
        }

        ValidateAttributes(element, ["id"]);
        var id = ParseIdentifier(element, identifiers);
        var title = ParseTitle(element);
        var children = ParseItemsExcludingTitle(element, identifiers, depth);
        return new XbelFolder(id, title, children);
    }

    private static XbelBookmark ParseBookmark(XElement element, ISet<long> identifiers)
    {
        ValidateAttributes(element, ["id", "href"]);
        var id = ParseIdentifier(element, identifiers);
        var url = element.Attribute("href")?.Value;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw CreateFormatException(element, "A bookmark must have a non-empty href attribute.");
        }

        var title = ParseTitle(element);
        EnsureNoItemElementsAfterTitle(element);
        return new XbelBookmark(id, title, url);
    }

    private static IReadOnlyList<XbelItem> ParseItemsExcludingTitle(
        XElement element,
        ISet<long> identifiers,
        int depth)
    {
        var title = element.Elements().First();
        var items = new List<XbelItem>();
        foreach (var node in element.Nodes())
        {
            if (IsWhitespace(node) || ReferenceEquals(node, title))
            {
                continue;
            }

            if (node is not XElement child)
            {
                throw CreateFormatException(node, "A folder contains an unsupported node.");
            }

            items.Add(child.Name.LocalName switch
            {
                "folder" when child.Name.Namespace == XNamespace.None =>
                    ParseFolder(child, identifiers, depth + 1),
                "bookmark" when child.Name.Namespace == XNamespace.None => ParseBookmark(child, identifiers),
                _ => throw CreateFormatException(child, $"Unsupported folder child '{child.Name}'.")
            });
        }

        return items;
    }

    private static string ParseTitle(XElement element)
    {
        var childElements = element.Elements().ToList();
        if (childElements.Count == 0
            || childElements[0].Name != XName.Get("title")
            || childElements.Count(child => child.Name == XName.Get("title")) != 1)
        {
            throw CreateFormatException(
                element,
                $"The {element.Name.LocalName} element must start with exactly one title element.");
        }

        var title = childElements[0];
        ValidateAttributes(title, []);
        if (title.Nodes().Any(node => node.GetType() != typeof(XText)))
        {
            throw CreateFormatException(title, "A title may contain text only.");
        }

        return title.Value;
    }

    private static void EnsureNoItemElementsAfterTitle(XElement bookmark)
    {
        var title = bookmark.Elements().First();
        foreach (var node in bookmark.Nodes())
        {
            if (IsWhitespace(node) || ReferenceEquals(node, title))
            {
                continue;
            }

            throw CreateFormatException(node, "A bookmark may contain only its title element.");
        }
    }

    private static long ParseIdentifier(XElement element, ISet<long> identifiers)
    {
        var value = element.Attribute("id")?.Value;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0
            || id > MaximumFloccusId)
        {
            throw CreateFormatException(
                element,
                $"The {element.Name.LocalName} element must have a positive numeric Floccus id.");
        }

        if (!identifiers.Add(id))
        {
            throw CreateFormatException(element, $"Floccus id {id} occurs more than once.");
        }

        return id;
    }

    private static void ValidateAttributes(XElement element, IReadOnlyCollection<string> allowedNames)
    {
        var attributes = element.Attributes().ToList();
        if (attributes.Any(attribute =>
                attribute.IsNamespaceDeclaration
                || attribute.Name.Namespace != XNamespace.None
                || !allowedNames.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
            || allowedNames.Any(name => element.Attribute(name) is null))
        {
            throw CreateFormatException(
                element,
                $"The {element.Name.LocalName} element has missing or unsupported attributes.");
        }
    }

    private static void ValidateElementName(XElement element, string expectedName)
    {
        if (element.Name != XName.Get(expectedName))
        {
            throw CreateFormatException(element, $"Expected the {expectedName} element.");
        }
    }

    private static void ValidateHighestId(long highestId, IEnumerable<long> identifiers)
    {
        var greatestUsedId = identifiers.DefaultIfEmpty(0).Max();
        if (highestId < greatestUsedId)
        {
            throw new XbelFormatException(
                $"Floccus highestId {highestId} is lower than the greatest used id {greatestUsedId}.");
        }
    }

    public void Validate(XbelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.HighestId < 0 || document.HighestId > MaximumFloccusId)
        {
            throw new XbelFormatException("Floccus highestId is outside the supported range.");
        }

        if (document.Items is null)
        {
            throw new XbelFormatException("The XBEL root item collection is missing.");
        }

        var identifiers = new HashSet<long>();
        ValidateModelItems(document.Items, identifiers, depth: 0);
        ValidateHighestId(document.HighestId, identifiers);
    }

    private static void ValidateModelItems(
        IReadOnlyList<XbelItem> items,
        ISet<long> identifiers,
        int depth)
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new XbelFormatException("An XBEL item collection contains a null item.");
            }

            if (item.Id <= 0 || item.Id > MaximumFloccusId || !identifiers.Add(item.Id))
            {
                throw new XbelFormatException(
                    $"XBEL item id {item.Id} is invalid or occurs more than once.");
            }

            if (item.Title is null)
            {
                throw new XbelFormatException($"XBEL item {item.Id} has no title.");
            }

            switch (item)
            {
                case XbelBookmark bookmark when string.IsNullOrWhiteSpace(bookmark.Url):
                    throw new XbelFormatException($"XBEL bookmark {item.Id} has no URL.");
                case XbelBookmark:
                    break;
                case XbelFolder folder when folder.Children is null:
                    throw new XbelFormatException($"XBEL folder {item.Id} has no child collection.");
                case XbelFolder folder:
                    if (depth >= MaximumFolderDepth)
                    {
                        throw new XbelFormatException(
                            $"XBEL folder nesting exceeds the supported depth of {MaximumFolderDepth}.");
                    }

                    ValidateModelItems(folder.Children, identifiers, depth + 1);
                    break;
                default:
                    throw new XbelFormatException(
                        $"XBEL item {item.Id} has unsupported type '{item.GetType().Name}'.");
            }
        }
    }

    private static XElement SerializeItem(XbelItem item)
    {
        return item switch
        {
            XbelBookmark bookmark => new XElement(
                "bookmark",
                new XAttribute("href", bookmark.Url),
                new XAttribute("id", bookmark.Id.ToString(CultureInfo.InvariantCulture)),
                new XElement("title", bookmark.Title)),
            XbelFolder folder => new XElement(
                "folder",
                new XAttribute("id", folder.Id.ToString(CultureInfo.InvariantCulture)),
                new XElement("title", folder.Title),
                folder.Children.Select(SerializeItem)),
            _ => throw new XbelFormatException(
                $"XBEL item {item.Id} has unsupported type '{item.GetType().Name}'.")
        };
    }

    private static bool IsWhitespace(XNode node)
    {
        return node is XText text && string.IsNullOrWhiteSpace(text.Value);
    }

    private static XbelFormatException CreateFormatException(XObject node, string message)
    {
        var lineInfo = (IXmlLineInfo)node;
        return lineInfo.HasLineInfo()
            ? new XbelFormatException($"{message} Line {lineInfo.LineNumber}, position {lineInfo.LinePosition}.")
            : new XbelFormatException(message);
    }

    [GeneratedRegex(
        @"^\s*-\s*highestId\s*:\s*([0-9]+)\s*:\s*for Floccus bookmark sync browser extension\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HighestIdCommentRegex();
}
