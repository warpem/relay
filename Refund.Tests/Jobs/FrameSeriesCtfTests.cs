using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.SingleAxisScatter;
using Refund.DataModel;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using Refund.Jobs.Fs.MotionCtf.CTF2D;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class FrameSeriesCtfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-fs-thickness-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "grid")]
    [InlineData(true, "c_grid")]
    public void CtfGrid_UsesTwoSpatialDimensionsAndLoadsLegacySavedGrids(bool combined, string flag)
    {
        JobRegistry.EnsurePopulated();
        Job job = combined ? new MotionAndCTF2D() : new CTF2D();
        job.Id = 12;
        job.Space = new Space { RootDirectory = _root };

        var property = job.GetType().GetProperty("CTFGridDims")!;
        Assert.IsType<UiInt2>(property.GetCustomAttribute<UiFieldBase>());
        Assert.Equal(new int2(1), property.GetValue(job));
        Assert.Equal("1x1", job.ComposeCommandArguments()[flag]);

        job.ReadFromJson(JsonNode.Parse("{\"CTFGridDims\":[5,6,40]}")!);
        Assert.Equal(new int2(5, 6), property.GetValue(job));
        Assert.Equal("5x6", job.ComposeCommandArguments()[flag]);
        Assert.Equal("[5,6]", job.ToJson()["CTFGridDims"]!.ToJsonString());

        if (job is MotionAndCTF2D motionAndCtf)
        {
            motionAndCtf.MotionGridDims = new int3(5, 6, 40);
            Assert.Equal("5x6x40", job.ComposeCommandArguments()["m_grid"]);
        }
    }

    [Fact]
    public void MotionAndCtfProcessesOnlyTheMoviesASelectionLists()
    {
        JobRegistry.EnsurePopulated();
        var space = new Space { RootDirectory = _root };
        var job = new MotionAndCTF2D { Id = 12, Space = space };
        Assert.False(job.ComposeCommandArguments().ContainsKey("input_data"));
        // A Warp export's micrographs are a selection of the session's folder, listed beside its STAR.
        string list = Path.Combine(_root, "7", "movies.txt");
        var set = new Refund.JobResources.MicrographSet { DataSetFs = new Refund.JobResources.DataSetFs(), InputListPath = list };
        var source = new PortOut(new Refund.Jobs.Common.Notes.Note.Note { Id = 7, Space = space }, typeof(Refund.JobResources.MicrographSet), "Micrographs", "Micrographs", _ => set);
        var input = job.PortsIn[MotionAndCTF2D.PortInMicrographs];
        input.Edges.Add(new Edge { Source = source, Target = input });
        Assert.Equal(space.GetRelativePath(list), job.ComposeCommandArguments()["input_data"]);
        set.InputListPath = "";
        Assert.False(job.ComposeCommandArguments().ContainsKey("input_data"));
    }

    [Fact]
    public async Task Overview_LoadsThicknessFromSummaryAndRefreshesWithoutReadingXml()
    {
        JobRegistry.EnsurePopulated();
        var job = new MotionAndCTF2D { Id = 12, Space = new Space { RootDirectory = _root } };
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        void WriteSummary(double thickness) => File.WriteAllText(job.ResProcessedItemsJson, JsonSerializer.Serialize(new[]
        {
            new { Path = "fitted.mrc", Phase = 0.25, CtfSpecimenThicknessAngstrom = (double?)thickness },
            new { Path = "legacy.mrc", Phase = 0.0, CtfSpecimenThicknessAngstrom = (double?)null },
            new { Path = "missing.mrc", Phase = 0.0, CtfSpecimenThicknessAngstrom = (double?)null },
            new { Path = "zero.mrc", Phase = 0.0, CtfSpecimenThicknessAngstrom = (double?)null }
        }));
        WriteSummary(1234.25);
        // XML deliberately disagrees with the summary. Startup must not consult it.
        var movie = new Movie(Path.Combine(job.DirectoryPath, "fitted.mrc")) { CTFSpecimenThicknessAngstrom = 999M };
        movie.SaveMeta();
        File.WriteAllText(job.FrameSeriesXmlFile("legacy.mrc"), "<Movie />");
        new Movie(Path.Combine(job.DirectoryPath, "zero.mrc")).SaveMeta();

        var view = new MotionAndCTF2DExpandedView();
        Field("_job").SetValue(view, job.AsReadOnly());
        await Load(view);

        var items = (List<WarpTools.MiniJsonFsItem>)Field("_processedItems").GetValue(view)!;
        var point = Assert.Single((List<ScatterPoint>)Field("_pointsCtfThickness").GetValue(view)!);
        Assert.Equal(1234.25, point.Value);
        Assert.Same(items[0], point.Metadata); // Clicking the point must select the matching frame series.
        Assert.Equal(1234.25, items[0].CtfSpecimenThicknessAngstrom);
        Assert.Equal(0.25, items[0].Phase);
        Assert.All(items.Skip(1), item => Assert.Null(item.CtfSpecimenThicknessAngstrom));

        File.Delete(movie.XMLPath);
        WriteSummary(987);
        await Load(view);
        point = Assert.Single((List<ScatterPoint>)Field("_pointsCtfThickness").GetValue(view)!);
        Assert.Equal(987, point.Value);
    }

    private static FieldInfo Field(string name) => typeof(MotionAndCTF2DExpandedView)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task Load(MotionAndCTF2DExpandedView view) => (Task)typeof(MotionAndCTF2DExpandedView)
        .GetMethod("LoadData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
