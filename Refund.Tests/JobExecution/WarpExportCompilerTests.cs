using System.Reflection;
using System.Text.Json;
using Refund.Services.Warp;
using Serilog;
using Warp;
using Warp.Tools;

namespace Refund.Tests.JobExecution;

public sealed class WarpExportCompilerTests
{
    [Fact]
    public async Task ParticleExportUsesCapturedLimitsOriginalStackIndicesAndPhysicalSampling()
    {
        string root = Path.Combine(Path.GetTempPath(), "warp session export " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var identity = new WarpSessionIdentity(1, 1, 1, root, Path.Combine(root, "tasks"));
        using var manager = new WarpSessionManager(Log.Logger);
        var session = manager.GetOrCreate(identity);
        session.Apply(new() { InputPath = root, OutputPath = root, Pixel = 1, TargetPixelSize = 2 });
        string processing = Path.Combine(root, "unique_movie name.eer");
        var metadata = new Movie(processing, root, [])
        {
            CTF = new CTF { Defocus = 1.5m, DefocusDelta = .2m, DefocusAngle = 23, PhaseShift = .1m },
            CTFResolutionEstimate = 3,
            OptionsCTF = new ProcessingOptionsMovieCTF { Dimensions = new float3(1000, 1000, 1) },
            GridCTFDefocus = new CubicGrid(new int3(1), [1.5f])
        };
        metadata.SaveMeta();
        Directory.CreateDirectory(metadata.AverageDir);
        File.WriteAllBytes(metadata.AveragePath, []);
        File.WriteAllBytes(processing, []);
        string stack = Path.Combine(root, "particle stack.mrcs");
        File.WriteAllBytes(stack, []);
        string picked = Path.Combine(root, "picked.star");
        File.WriteAllText(picked, "data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4\n12 18 0.2 4\n24 32 0.9 9\n40 50 0.8 30\n");
        var row = new WarpMovie
        {
            Index = 0, Id = "unique", Name = "movie name.eer", InputPath = processing, ProcessingPath = processing,
            MetadataPath = metadata.XMLPath, AveragePath = metadata.AveragePath, ParticleStarPath = picked, ParticleStackPath = stack,
            EnhancedPixelSize = 5, PixelSize = 2, Ctf = 3, Motion = 1, Astigmatism = .2, Ice = 100, Picks = 3, Done = WarpStage.All,
            Completed = new(Field<Dictionary<string, ulong>>(session, "_stageKeys"))
        };
        Field<List<WarpMovie>>(session, "_movies").Add(row);
        session.SetDisposition([0], WarpDisposition.Auto);
        session.SetParticleRange(.5, 1, 0, 10);
        try
        {
            var export = await session.ExportAsync(true);
            var table = new Star(export.StarPath, "particles");
            Assert.Equal(1, table.RowCount);
            Assert.Equal("000002@" + stack, Assert.Single(table.GetColumn("rlnImageName")));
            Assert.Equal(60, double.Parse(Assert.Single(table.GetColumn("rlnCoordinateX")), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(80, double.Parse(Assert.Single(table.GetColumn("rlnCoordinateY")), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(16000, double.Parse(Assert.Single(table.GetColumn("rlnDefocusU")), System.Globalization.CultureInfo.InvariantCulture), 2);
            var (legacy, isRelion3) = Star.LoadRelion3Particles(export.StarPath);
            Assert.False(isRelion3);
            Assert.Equal(2m, Assert.Single(legacy.GetRelionCTF()).PixelSize);
            Assert.True(export.HasParticleData);
            Assert.Equal(1, export.ParticleCount);
            var mini = JsonSerializer.Deserialize<List<Refund.Utils.WarpTools.MiniJsonFsItem>>(File.ReadAllText(export.ProcessedItemsJson))!;
            Assert.Equal("unique_movie name.eer", Assert.Single(mini).Path);
            Assert.True(File.Exists(Path.Combine(export.ProcessingDirectory, Movie.ToXMLPath(row.ProcessingPath))));
            Assert.True(File.Exists(Path.Combine(export.DataDirectory, Path.GetFileName(processing))));
            session.SetParticleRange(0, 1, 0, null);
            Assert.Equal(1, new Star(export.StarPath, "particles").RowCount);
            session.Apply(session.Settings with { Box = 512 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportAsync(true));
            var micrographs = await session.ExportAsync(false);
            Assert.Equal(1, new Star(micrographs.StarPath, "micrographs").RowCount);
            Assert.False(micrographs.HasParticleData);
        }
        finally { manager.Dispose(); Directory.Delete(root, true); }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
}
