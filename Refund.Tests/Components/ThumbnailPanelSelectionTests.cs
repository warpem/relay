using Refund.Components.ThumbnailPanel;

namespace Refund.Tests.Components;

/// <summary>
/// ThumbnailData identifies items by reference, so a list holding two entries with the same image
/// still selects and highlights exactly one of them. The cost is that a selection has to be
/// re-resolved whenever the parent rebuilds its list, which ResolveInCurrentList handles.
/// </summary>
public class ThumbnailPanelSelectionTests
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

    private static List<ThumbnailPanel.ThumbnailItem> Window(List<ThumbnailData> thumbnails, ThumbnailData selected)
    {
        return ThumbnailPanel.BuildVisibleItems(thumbnails,
                                                scrollLeft: 0,
                                                clientWidth: 8 * ThumbnailWidth,
                                                thumbnailWidth: ThumbnailWidth,
                                                selectedThumbnail: selected,
                                                out _,
                                                out _);
    }

    [Fact]
    public void EntriesSharingAnImagePathAreNotEqual()
    {
        var thumbnails = MakeThumbnails("shared.png", "shared.png");

        Assert.NotEqual(thumbnails[0], thumbnails[1]);
        Assert.Equal(1, thumbnails.IndexOf(thumbnails[1]));
    }

    [Fact]
    public void OnlyTheSelectedTwinIsHighlighted()
    {
        // With ImagePath equality both of these lit up, and clicking either resolved to the first.
        var thumbnails = MakeThumbnails("a.png", "shared.png", "shared.png", "d.png");

        var items = Window(thumbnails, selected: thumbnails[2]);

        Assert.Single(items.Where(item => item.IsSelected));
        Assert.True(items.Single(item => item.Index == 2).IsSelected);
        Assert.False(items.Single(item => item.Index == 1).IsSelected);
    }

    [Fact]
    public void SelectionSurvivesAListRebuildThatReplacesInstances()
    {
        // The reload path: parents rebuild the list wholesale on every job update, so the old
        // instance is gone and the selection must be re-pointed rather than dropped.
        var original = MakeThumbnails("a.png", "b.png", "c.png");
        var rebuilt = MakeThumbnails("a.png", "b.png", "c.png");

        var resolved = ThumbnailPanel.ResolveInCurrentList(rebuilt, original[1]);

        Assert.NotNull(resolved);
        Assert.Same(rebuilt[1], resolved);
    }

    [Fact]
    public void RebuildResolvesEachTwinToItsOwnEntry()
    {
        var original = MakeThumbnails("shared.png", "shared.png");
        var rebuilt = MakeThumbnails("shared.png", "shared.png");

        Assert.Same(rebuilt[0], ThumbnailPanel.ResolveInCurrentList(rebuilt, original[0]));
        Assert.Same(rebuilt[1], ThumbnailPanel.ResolveInCurrentList(rebuilt, original[1]));
    }

    [Fact]
    public void ResolveReturnsTheSameInstanceWhenItIsStillInTheList()
    {
        var thumbnails = MakeThumbnails("a.png", "b.png");

        Assert.Same(thumbnails[1], ThumbnailPanel.ResolveInCurrentList(thumbnails, thumbnails[1]));
    }

    [Fact]
    public void ResolveFallsBackToImagePathWhenPositionsShift()
    {
        var original = MakeThumbnails("a.png", "b.png");
        var shifted = MakeThumbnails("new.png", "a.png", "b.png");

        var resolved = ThumbnailPanel.ResolveInCurrentList(shifted, original[1]);

        Assert.Same(shifted[2], resolved);
    }

    [Fact]
    public void ResolveReturnsNullWhenTheItemIsGone()
    {
        var original = MakeThumbnails("a.png", "b.png");
        var rebuilt = MakeThumbnails("c.png");

        Assert.Null(ThumbnailPanel.ResolveInCurrentList(rebuilt, original[1]));
        Assert.Null(ThumbnailPanel.ResolveInCurrentList(rebuilt, null));
        Assert.Null(ThumbnailPanel.ResolveInCurrentList(null, original[0]));
    }

    [Fact]
    public void NothingIsHighlightedWhenSelectionIsNull()
    {
        var thumbnails = MakeThumbnails("a.png", "b.png");

        Assert.Empty(Window(thumbnails, selected: null).Where(item => item.IsSelected));
    }
}
