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
