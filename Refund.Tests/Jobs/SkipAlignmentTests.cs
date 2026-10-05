using System.Globalization;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Utils;
using Warp;
using Warp.Tools;
using SkipAlignmentJob = Refund.Jobs.Ts.Alignment.SkipAlignment.SkipAlignment;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class SkipAlignmentTests
{
    [Fact]
    public void Parameters_AreAvailableAndPersisted()
    {
        JobRegistry.EnsurePopulated();
        var job = new SkipAlignmentJob();
        Assert.Equal(0.0m, job.TiltAxisAngle);
        Assert.IsAssignableFrom<ILocalJob>(job);
        Assert.Equal(JobQueueType.Local, job.QueueType);
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(typeof(SkipAlignmentJob), Job.Types[job.TypeGuid]);

        job.TiltAxisAngle = -27.25m;
        var json = new JsonObject();
        job.WriteToJson(json);
        var restored = new SkipAlignmentJob();
        restored.ReadFromJson(json);
        Assert.Equal(job.TiltAxisAngle, restored.TiltAxisAngle);
        Assert.Equal(job.TiltAxisAngle,
            Assert.IsType<Refund.Jobs.Ts.Alignment.SkipAlignment.ReadOnlySkipAlignment>(restored.AsReadOnly()).TiltAxisAngle);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-27.25)]
    public void RunLocal_WritesFreshZeroShiftMetadataAndDownstreamSettings(double angle)
    {
        JobRegistry.EnsurePopulated();
        string root = Path.Combine(Path.GetTempPath(), "relay-skip-alignment-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var dataSet = new DataSetTs
            {
                ItemCount = 2,
                DataDirectory = input,
                TomogramDimensions = new int3(100, 120, 80),
                Micrographs = new MicrographSet { DataSetFs = new DataSetFs { PixelSize = 1.5m } }
            };
            var job = new SkipAlignmentJob
            {
                Space = new Space { RootDirectory = root },
                Id = 17,
                TiltAxisAngle = (decimal)angle
            };
            var port = job.PortsIn[SkipAlignmentJob.PortInDataSetTs];
            var source = new PortOut(job, typeof(DataSetTs), "src", "src", _ => dataSet);
            port.Edges.Add(new Edge { Source = source, Target = port });

            foreach (string name in new[] { "series_a", "series_b" })
            {
                var table = new Star(new[] { "wrpMovieName", "wrpAngleTilt", "wrpDose", "wrpAxisAngle", "wrpAxisOffsetX", "wrpAxisOffsetY" });
                table.AddRow(new[] { "../images/tilt1.mrc", "-30", "1.5", "85", "12", "-9" });
                table.AddRow(new[] { "../images/tilt2.mrc", "0", "3.0", "86", "-15", "8" });
                table.AddRow(new[] { "../images/tilt3.mrc", "30", "4.5", "87", "21", "7" });
                table.Save(Path.Combine(input, name + ".tomostar"));
                // Input-side XML must not override the TOMOSTAR definition either.
                File.WriteAllText(Path.Combine(input, name + ".xml"), "input metadata stays untouched");
            }

            // Running again must discard corrections left in this job's XML files.
            for (int run = 0; run < 2; run++)
            {
                job.RunLocal(CancellationToken.None);
                Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
                foreach (string name in new[] { "series_a", "series_b" })
                {
                    string output = Path.Combine(job.DirectoryPath, name + ".tomostar");
                    Assert.True(File.Exists(Path.ChangeExtension(output, ".xml")));
                    Assert.False(File.Exists(output));
                    var series = new TiltSeries(output);
                    Assert.Equal(new float[] { -30, 0, 30 }, series.Angles);
                    Assert.Equal(new float[] { 1.5f, 3, 4.5f }, series.Dose);
                    Assert.Equal(new[] { "../images/tilt1.mrc", "../images/tilt2.mrc", "../images/tilt3.mrc" }, series.TiltMoviePaths);
                    Assert.All(series.TiltAxisAngles, value => Assert.Equal((float)angle, value));
                    Assert.All(series.TiltAxisOffsetX, value => Assert.Equal(0f, value));
                    Assert.All(series.TiltAxisOffsetY, value => Assert.Equal(0f, value));
                    Assert.All(series.UseTilt, value => Assert.True(value));
                    Assert.All(series.FOVFraction, value => Assert.Equal(1f, value));
                    Assert.All(series.GridMovementX.FlatValues, value => Assert.Equal(0f, value));
                    Assert.Equal(Path.Combine(input, name + ".tomostar"), series.DataPath);
                    Assert.Equal("input metadata stays untouched", File.ReadAllText(Path.Combine(input, name + ".xml")));

                    if (run == 0)
                    {
                        series.GridMovementX = new CubicGrid(new int3(1), new float[] { 42 });
                        series.TiltAxisOffsetX[0] = 99;
                        series.UseTilt[0] = false;
                        series.SaveMeta();
                    }
                }
            }

            var options = new OptionsWarp();
            options.Load(Path.Combine(job.DirectoryPath, "processing.settings"));
            Assert.Equal(input, options.Import.DataFolder);
            Assert.Equal(job.DirectoryPath, options.Import.ProcessingFolder);
            Assert.Equal("*.tomostar", options.Import.Extension);
            Assert.Equal(100, options.Tomo.DimensionsX);
            Assert.Equal(1.5m, options.Import.PixelSize);

            var result = Assert.IsType<TiltSeriesSet>(job.PortsOut[SkipAlignmentJob.PortOutTs].GetResource());
            Assert.True(result.HasMetadata);
            Assert.Equal(job.DirectoryPath, result.LatestMetadataDirectory);
            Assert.Equal(Path.Combine(job.DirectoryPath, "processing.settings"), result.DataSet.SettingsPath);
            Assert.Same(dataSet.Micrographs, result.DataSet.Micrographs);
            Assert.False(result.HasTiltStacks);
            Assert.Equal(2, JsonNode.Parse(File.ReadAllText(result.ProcessedItemsJson))!.AsArray().Count);
            Assert.Equal(2, ResourceItemCounter.CountOutputs(job)[SkipAlignmentJob.PortOutTs]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RunLocal_ThrowsForCancellationBeforeWritingFiles()
    {
        var job = new SkipAlignmentJob();
        Assert.Throws<OperationCanceledException>(() => job.RunLocal(new CancellationToken(true)));
    }
}
