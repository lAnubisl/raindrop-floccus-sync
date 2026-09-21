using RaindropToFloccus.Helpers;
using RaindropToFloccus.Models;
using Xunit;

namespace RaindropToFloccus.Tests;

public sealed class FloccusXbelSerializerTests
{
    private readonly FloccusXbelSerializer _serializer = new();

    [Fact]
    public void Serialize_uses_the_exact_XML_declaration_required_by_Floccus()
    {
        var content = _serializer.Serialize(CreateDocument());

        Assert.StartsWith(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("encoding=\"utf-8\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_uses_the_same_root_layout_as_Floccus()
    {
        var content = _serializer.Serialize(CreateDocument());

        Assert.Equal(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE xbel PUBLIC "+//IDN python.org//DTD XML Bookmark Exchange Language 1.0//EN//XML" "http://pyxml.sourceforge.net/topics/dtds/xbel.dtd">
            <xbel version="1.0">
            <!--- highestId :2: for Floccus bookmark sync browser extension -->
            <folder id="1">
              <title>Unicode 🌧 folder</title>
              <bookmark href="https://example.com/?a=1&amp;b=2" id="2">
                <title>Example &amp; bookmark</title>
              </bookmark>
            </folder>
            </xbel>
            """.ReplaceLineEndings("\n"),
            content);
    }

    [Fact]
    public void Serialize_produces_a_document_that_round_trips_without_losing_values()
    {
        var expected = CreateDocument();

        var actual = _serializer.Parse(_serializer.Serialize(expected));

        Assert.Equal(expected.HighestId, actual.HighestId);
        var folder = Assert.IsType<XbelFolder>(Assert.Single(actual.Items));
        Assert.Equal("Unicode 🌧 folder", folder.Title);
        var bookmark = Assert.IsType<XbelBookmark>(Assert.Single(folder.Children));
        Assert.Equal(2, bookmark.Id);
        Assert.Equal("Example & bookmark", bookmark.Title);
        Assert.Equal("https://example.com/?a=1&b=2", bookmark.Url);
    }

    [Fact]
    public void Serialize_uses_Floccus_entities_for_quotes_and_apostrophes()
    {
        var document = new XbelDocument(
            HighestId: 1,
            Items:
            [
                new XbelBookmark(
                    1,
                    "Title with 'apostrophe' and \"quotes\"",
                    "https://example.com/?single='value'&double=\"value\"")
            ]);

        var content = _serializer.Serialize(document);

        Assert.Contains(
            "href=\"https://example.com/?single=&apos;value&apos;&amp;double=&quot;value&quot;\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "<title>Title with &apos;apostrophe&apos; and &quot;quotes&quot;</title>",
            content,
            StringComparison.Ordinal);

        var bookmark = Assert.IsType<XbelBookmark>(Assert.Single(_serializer.Parse(content).Items));
        Assert.Equal(document.Items[0], bookmark);
    }

    private static XbelDocument CreateDocument() => new(
        HighestId: 2,
        Items:
        [
            new XbelFolder(
                1,
                "Unicode 🌧 folder",
                [new XbelBookmark(2, "Example & bookmark", "https://example.com/?a=1&b=2")])
        ]);
}
