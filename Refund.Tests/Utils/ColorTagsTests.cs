using Refund.Utils;

namespace Refund.Tests.Utils;

public sealed class ColorTagsTests
{
    [Fact]
    public void ColorsSortInPaletteOrderNotByHexCode()
    {
        string[] tags = ["#F5CDA8", "#A8B8F5", "#F5A8A8", "#A8F5B8"]; // orange, blue, red, green

        var sorted = tags.OrderBy(ColorTags.SortIndex).ToArray();

        Assert.Equal(["#F5A8A8", "#F5CDA8", "#A8F5B8", "#A8B8F5"], sorted); // red, orange, green, blue
    }

    [Fact]
    public void MatchingIgnoresCaseAndSurroundingWhitespace()
    {
        Assert.Equal(0, ColorTags.SortIndex(" #f5a8a8 "));
    }

    [Fact]
    public void UnknownColorsSortAfterThePaletteAndEmptyTagsLast()
    {
        int unknown = ColorTags.SortIndex("#123456");

        Assert.True(unknown > ColorTags.SortIndex("#F5A8E6")); // pink, last in the palette
        Assert.True(ColorTags.SortIndex(null) > unknown);
        Assert.Equal(ColorTags.SortIndex(null), ColorTags.SortIndex("  "));
    }
}
