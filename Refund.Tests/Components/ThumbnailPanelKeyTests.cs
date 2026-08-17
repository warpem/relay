using Refund.Components.ThumbnailPanel;

namespace Refund.Tests.Components;

/// <summary>
/// The render-tree key of a Thumbnail must be unique among its siblings, otherwise Blazor throws
/// "More than one sibling ... has the same key value" while diffing and tears down the circuit.
/// </summary>
public class ThumbnailPanelKeyTests
{
    private const int ThumbnailWidth = 132;

    private static List<ThumbnailData> MakeThumbnails(params string[] imagePaths)
    {
        return imagePaths.Select((path, i) => new ThumbnailData
        {
            Index = i,
            ImagePath = path,
            Status = ProcessingStatus.Processed
        }).ToList();
    }

    private static List<ThumbnailPanel.ThumbnailItem> Window(List<ThumbnailData> thumbnails, double scrollLeft)
    {
        return ThumbnailPanel.BuildVisibleItems(thumbnails,
                                                scrollLeft,
                                                clientWidth: 6 * ThumbnailWidth,
                                                thumbnailWidth: ThumbnailWidth,
                                                selectedThumbnail: null,
                                                out _,
                                                out _);
    }

    [Fact]
    public void KeysAreUnique_WhenTwoThumbnailsShareAnImagePath()
    {
        // Reproduces the PACE dataset that emitted "x.mdoc" and "x_unsorted.mdoc": two distinct
        // tilt series whose middle tilt, and therefore whose thumbnail, is the same file.
        var thumbnails = MakeThumbnails("a.png", "shared.png", "shared.png", "d.png");

        var keys = Window(thumbnails, scrollLeft: 0).Select(item => item.Key).ToList();

        Assert.Equal(4, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void KeysAreUnique_AtEveryScrollPosition_WhenAllImagePathsAreIdentical()
    {
        // Worst case: the window always contains duplicates no matter where it lands.
        var thumbnails = MakeThumbnails(Enumerable.Repeat("same.png", 60).ToArray());

        for (int scrollLeft = 0; scrollLeft < thumbnails.Count * ThumbnailWidth; scrollLeft += ThumbnailWidth)
        {
            var keys = Window(thumbnails, scrollLeft).Select(item => item.Key).ToList();

            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void KeysAreStable_WhileScrollingAnUnchangedList()
    {
        // Keys must stay tied to content so that merely scrolling reuses components rather than
        // recreating them.
        var thumbnails = MakeThumbnails(Enumerable.Range(0, 60).Select(i => $"img{i}.png").ToArray());

        var before = Window(thumbnails, scrollLeft: 10 * ThumbnailWidth);
        var after = Window(thumbnails, scrollLeft: 11 * ThumbnailWidth);

        var overlap = before.Select(item => item.Key).Intersect(after.Select(item => item.Key)).ToList();

        Assert.NotEmpty(overlap);
        foreach (var key in overlap)
        {
            Assert.Equal(before.Single(item => item.Key.Equals(key)).Data.ImagePath,
                         after.Single(item => item.Key.Equals(key)).Data.ImagePath);
        }
    }

    [Fact]
    public void KeyChanges_WhenAnIndexStartsShowingADifferentImage()
    {
        // If the list shifts, the component at a given index must not be reused for another image,
        // which is what keying on the index alone would have allowed.
        var original = MakeThumbnails("a.png", "b.png", "c.png", "d.png");
        var shifted = MakeThumbnails("new.png", "a.png", "b.png", "c.png", "d.png");

        var keyAtIndex1Before = Window(original, scrollLeft: 0).Single(item => item.Index == 1).Key;
        var keyAtIndex1After = Window(shifted, scrollLeft: 0).Single(item => item.Index == 1).Key;

        Assert.NotEqual(keyAtIndex1Before, keyAtIndex1After);
    }
}
