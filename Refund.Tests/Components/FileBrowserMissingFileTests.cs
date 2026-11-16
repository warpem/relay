using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Refund.Components.FileBrowser;

namespace Refund.Tests.Components;

/// <summary>
/// A folder listing can outlive its files (e.g. scratch files of running jobs). Selecting such a
/// file must not throw during render, which would tear down the whole Blazor circuit.
/// </summary>
public sealed class FileBrowserMissingFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-filebrowser-" + Guid.NewGuid().ToString("N"));

    public FileBrowserMissingFileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MetadataOfAFileDeletedAfterListingIsEmptyInsteadOfThrowing()
    {
        string path = Path.Combine(_root, "hb-113");
        File.WriteAllText(path, "heartbeat");
        var item = new FileSystemItem(path);
        File.Delete(path);

        Assert.False(item.Exists);
        Assert.Equal(0, item.SizeInByte);
        Assert.Equal(DateTime.MinValue, item.CreationTime);
        Assert.Equal(DateTime.MinValue, item.LastWriteTime);
        Assert.Equal(0L, item.GetSortValue("Size"));
    }

    [Fact]
    public void RefreshNoticesADeletionAfterMetadataWasCached()
    {
        string path = Path.Combine(_root, "log.txt");
        File.WriteAllText(path, "12345");
        var item = new FileSystemItem(path);
        Assert.Equal(5, item.SizeInByte);

        File.Delete(path);
        Assert.True(item.Exists);
        item.Refresh();
        Assert.False(item.Exists);
        Assert.Equal(0, item.SizeInByte);
    }

    [Fact]
    public void AFileDeletedBeforeTheItemIsCreatedIsNotTreatedAsHidden()
    {
        var item = new FileSystemItem(Path.Combine(_root, "never-existed"));
        Assert.False(item.IsHidden);
        Assert.False(item.Exists);
    }

    [Fact]
    public async Task DetailsColumnRendersAMissingFileInsteadOfCrashing()
    {
        string path = Path.Combine(_root, "hb-113");
        File.WriteAllText(path, "heartbeat");
        var item = new FileSystemItem(path);
        Assert.True(item.Exists); // metadata cached while the file was there
        File.Delete(path);

        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DetailsColumn>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DetailsColumn.Items)] = new List<FileSystemItem> { item },
                [nameof(DetailsColumn.EditAction)] = (RenderFragment)(b => b.AddMarkupContent(0, "<span>edit-action</span>")),
            }));
            return component.ToHtmlString();
        });

        Assert.Contains("hb-113", html);
        Assert.Contains("no longer exists", html);
        Assert.DoesNotContain("edit-action", html);
        Assert.DoesNotContain("Download file", html);
    }
}
