using Microsoft.Extensions.Logging.Abstractions;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Services;

namespace Refund.Tests.DataModel;

public class DownloadableTests
{
    [Fact]
    public void JobFilenameIncludesProjectAndSpaceAndPreservesOriginalBasename()
    {
        var downloadable = new Downloadable("Half-map 1", "", "/results/J42/half1_class003.mrc");
        Assert.Equal("P12_S34_half1_class003.mrc", downloadable.GetFileName(12, 34));
    }

    [Fact]
    public void ClassDownloadsHaveUniqueNamesAndKeepMetadataSeparate()
    {
        var maps = new MapList(Enumerable.Range(1, 3).Select(c => new Map(
            half1VolumePath: $"half1_class{c}.mrc", half2VolumePath: $"half2_class{c}.mrc",
            averageVolumePath: $"class{c}.mrc")).ToList(), "model.star");
        var downloads = maps.GetDownloadables().ToList();
        Assert.Equal(10, downloads.Select(d => d.Name).Distinct().Count());
        Assert.Equal("Class 1: Half-map 1", downloads[0].Name);
        Assert.Equal("Class 3: Average map", downloads[8].Name);
        Assert.Equal("class3.mrc", downloads[8].ServerPath);
        Assert.Equal("Model Metadata", downloads[9].Name);
    }

    [Fact]
    public void ExplicitClassIdsSurviveSubsetsAndSingleClassResults()
    {
        var maps = new MapList([new Map(averageVolumePath: "class5.mrc", classId: 5)]);
        Assert.Equal("Class 5: Average map", Assert.Single(maps.GetDownloadables()).Name);
        var reconstruction = new MapList([new Map(averageVolumePath: "map.mrc")]);
        Assert.Equal("Average map", Assert.Single(reconstruction.GetDownloadables()).Name);
    }

    [Fact]
    public void DownloadUrlEncodesFilenameWithoutChangingFileMapping()
    {
        var files = new FileService(NullLogger<FileService>.Instance);
        string url = files.GetUrl("/results/map.mrc");
        Assert.Equal($"{url}?downloadName=P1_S2_map%20%231.mrc",
            files.GetDownloadUrl("/results/map.mrc", "P1_S2_map #1.mrc"));
        Assert.True(files.TryGetPath(url.Split('/').Last(), out var path));
        Assert.Equal("/results/map.mrc", path);
        Assert.Equal(url, files.GetUrl(path));
    }
}
