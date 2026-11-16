using System.Reflection;
using System.Text.Json;
using Refund.Jobs.Fs.Warp;
using Refund.Services.Warp;
using Refund.Utils;
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
        session.Apply(new() { InputPath = root, OutputPath = root, Pixel = 1, TargetPixelSize = 2, ParticleBounds = new(.5, 1, 0, 10) });
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
        IOHelper.WriteMapFloat(metadata.AveragePath, new global::Warp.Headers.HeaderMRC { Dimensions = new int3(64, 48, 1), PixelSize = new float3(2) }, new float[64 * 48]);
        File.WriteAllBytes(processing, []);
        string stack = Path.Combine(root, "particle stack.mrcs");
        File.WriteAllBytes(stack, []);
        string picked = Path.Combine(root, "picked.star");
        File.WriteAllText(picked, "data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4\n12 18 0.2 4\n24 32 0.9 9\n40 50 0.8 30\n");
        // Extraction kept the one pick within the bounds, as the first image of the stack.
        File.WriteAllText(Path.ChangeExtension(stack, ".star"), "data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4\n24 32 0.9 9\n");
        var row = new WarpMovie
        {
            Index = 0, Id = "unique", Name = "movie name.eer", InputPath = processing, ProcessingPath = processing,
            MetadataPath = metadata.XMLPath, AveragePath = metadata.AveragePath, ParticleStarPath = picked, ParticleStackPath = stack,
            EnhancedPixelSize = 5, PixelSize = 2, Ctf = 3, Motion = 1, Astigmatism = .2, Ice = 100, Picks = 3, Particles = 1, Done = WarpStage.All,
            Completed = new(Field<Dictionary<string, ulong>>(session, "_stageKeys"))
        };
        Field<List<WarpMovie>>(session, "_movies").Add(row);
        session.SetDisposition([0], WarpDisposition.Auto);
        try
        {
            string exportDirectory = Path.Combine(root, "export job");
            var export = await session.ExportAsync(true, exportDirectory, root);
            Assert.Equal(Path.Combine(exportDirectory, "particles.star"), export.StarPath);
            // Three files, whose rows refer to the session's own movie metadata, stack and link to the raw movie, relative
            // to the space (here the session's folder) so they hold when it moves.
            Assert.Equal(["movies.txt", "particles.star", "processed_items.json"], Directory.GetFileSystemEntries(exportDirectory).Select(Path.GetFileName).Order().ToArray());
            Assert.Equal(root, export.ProcessingDirectory);
            Assert.Equal(Path.Combine(exportDirectory, "movies.txt"), export.InputListPath);
            Assert.Equal([Path.Combine("input", Path.GetFileName(processing))], File.ReadAllLines(export.InputListPath));
            var table = new Star(export.StarPath, "particles");
            Assert.Equal(1, table.RowCount);
            Assert.Equal("000001@particle stack.mrcs", Assert.Single(table.GetColumn("rlnImageName")));
            // A particle names its movie in the processing folder, as Warp does: its metadata is the same name ending in .xml.
            Assert.Equal("unique_movie name.eer", Assert.Single(table.GetColumn("rlnMicrographName")));
            Assert.Equal("1", Assert.Single(table.GetColumn("rlnOpticsGroup")));
            Assert.Null(table.GetColumn("rlnImagePixelSize")); // Optics columns are in the optics table.
            var optics = new Star(export.StarPath, "optics");
            Assert.Equal(1, optics.RowCount);
            Assert.Equal(["2", "2", "256", "2", "0"], new[] { "rlnImagePixelSize", "rlnMicrographPixelSize", "rlnImageSize", "rlnImageDimensionality", "rlnCtfDataAreCtfPremultiplied" }.Select(c => Assert.Single(optics.GetColumn(c))).ToArray());
            Assert.Equal(60, double.Parse(Assert.Single(table.GetColumn("rlnCoordinateX")), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(80, double.Parse(Assert.Single(table.GetColumn("rlnCoordinateY")), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(16000, double.Parse(Assert.Single(table.GetColumn("rlnDefocusU")), System.Globalization.CultureInfo.InvariantCulture), 2);
            var (merged, isRelion3) = Star.LoadRelion3Particles(export.StarPath);
            Assert.True(isRelion3);
            Assert.Equal(2m, Assert.Single(merged.GetRelionCTF()).PixelSize);
            Assert.True(export.HasParticleData);
            Assert.Equal(1, export.ParticleCount);
            var mini = JsonSerializer.Deserialize<List<Refund.Utils.WarpTools.MiniJsonFsItem>>(File.ReadAllText(export.ProcessedItemsJson))!;
            Assert.Equal("unique_movie name.eer", Assert.Single(mini).Path);
            // The export job's card shows the captured micrograph with its particle, in the average's pixels.
            var items = JsonSerializer.Deserialize<List<WarpTools.MiniJsonFsItem>>(File.ReadAllText(export.ProcessedItemsJson))!;
            var card = WarpExportCard.Create(export, items, export.StarPath);
            var sample = Assert.Single(card.Samples);
            Assert.Equal((64, 48), (sample.Width, sample.Height));
            Assert.Equal([60f], sample.X); Assert.Equal([80f], sample.Y);
            Assert.EndsWith(".png", sample.Thumbnail);
            Assert.Equal(3, card.MedianResolution);
            // Its log says what was captured and by which limits.
            var log = WarpExportJob.Describe(export, 7, card).ToArray();
            Assert.Contains(log, line => line.StartsWith("Exported from Warp session J7") && line.Contains("1 particles from 1 accepted micrographs"));
            Assert.Contains(log, line => line.StartsWith("Accepted: movies marked Keep, or passing the filters (CTF fit 0–5 Å, CTF signal ≥ 5 σ"));
            Assert.Contains(log, line => line.StartsWith("Particles: picks with correlation 0.5–1 and local power 0–10"));
            Assert.Contains(log, line => line == "Wrote particles.star (1 particles), processed_items.json and movies.txt.");
            Assert.NotNull(export.Filters);
            // New bounds outdate the extraction: no particles to export until it is redone, and earlier exports stay as they were.
            session.Apply(session.Settings with { ParticleBounds = new() });
            Assert.Equal(0, session.CountExportable(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportAsync(true, Path.Combine(root, "second"), root));
            Assert.Equal(1, new Star(export.StarPath, "particles").RowCount);
            // Exporting again into the same directory replaces its links and metadata copies.
            var micrographs = await session.ExportAsync(false, exportDirectory, root);
            micrographs = await session.ExportAsync(false, exportDirectory, root);
            Assert.Equal(1, new Star(micrographs.StarPath, "micrographs").RowCount);
            // Micrographs are the averages, which tools reading a micrograph STAR open.
            Assert.Equal(Path.Combine("average", "unique_movie name.mrc"), Assert.Single(new Star(micrographs.StarPath, "micrographs").GetColumn("rlnMicrographName")));
            Assert.Equal("2", Assert.Single(new Star(micrographs.StarPath, "optics").GetColumn("rlnMicrographPixelSize")));
            Assert.False(micrographs.HasParticleData);
            Assert.True(File.Exists(metadata.AveragePath));
            Assert.Empty(session.Exports);
        }
        finally { manager.Dispose(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExportsPutMicrographsInOpticsGroupsByEpuBeamShift()
    {
        string root = Path.Combine(Path.GetTempPath(), "warp-epu-export-" + Guid.NewGuid());
        string data = Path.Combine(root, "epu", "Images-Disc1", "GridSquare_1", "Data");
        Directory.CreateDirectory(data);
        var identity = new WarpSessionIdentity(1, 1, 1, root, Path.Combine(root, "tasks"));
        using var manager = new WarpSessionManager(Log.Logger);
        var session = manager.GetOrCreate(identity);
        session.Apply(new() { InputPath = data, OutputPath = root, Pixel = 1, TargetPixelSize = 2 });
        // Two shots of one hole, one of another hole, and one movie whose EPU XML is missing.
        (double X, double Y)?[] shifts = [(0, 0), (.1, 0), (.02, 0), null];
        var movies = Field<List<WarpMovie>>(session, "_movies");
        for (int i = 0; i < shifts.Length; i++)
        {
            string stem = $"FoilHole_1_Data_2_{i}_20250101_12000{i}";
            string input = Path.Combine(data, stem + "_EER.eer");
            File.WriteAllBytes(input, []);
            if (shifts[i] is { } shift) File.WriteAllText(Path.Combine(data, stem + ".xml"), Refund.Tests.Warp.WarpEpuTests.Xml(shift.X, shift.Y));
            string processing = Path.Combine(root, "processing", $"{i}_{stem}_EER.eer");
            var metadata = new Movie(processing, root, []) { CTF = new CTF { Defocus = 1.5m }, CTFResolutionEstimate = 3, OptionsCTF = new ProcessingOptionsMovieCTF { Dimensions = new float3(1000, 1000, 1) } };
            metadata.SaveMeta();
            Directory.CreateDirectory(metadata.AverageDir);
            IOHelper.WriteMapFloat(metadata.AveragePath, new global::Warp.Headers.HeaderMRC { Dimensions = new int3(8, 8, 1), PixelSize = new float3(2) }, new float[64]);
            movies.Add(new WarpMovie { Index = i, Id = $"m{i}", Name = Path.GetFileName(input), InputPath = input, ProcessingPath = processing, MetadataPath = metadata.XMLPath,
                AveragePath = metadata.AveragePath, Ctf = 3, Motion = 1, Done = WarpStage.Motion | WarpStage.Ctf, Completed = new(Field<Dictionary<string, ulong>>(session, "_stageKeys")) });
        }
        session.SetDisposition([0, 1, 2, 3], WarpDisposition.Keep);
        try
        {
            var export = await session.ExportAsync(false, Path.Combine(root, "export"), root);
            var optics = new Star(export.StarPath, "optics");
            Assert.Equal(["1", "2", "3"], optics.GetColumn("rlnOpticsGroup"));
            Assert.Equal(["beamShift1", "beamShift2", "noEpuMetadata"], optics.GetColumn("rlnOpticsGroupName"));
            Assert.Equal(["1", "2", "1", "3"], new Star(export.StarPath, "micrographs").GetColumn("rlnOpticsGroup"));
            Assert.Equal(new WarpOpticsGroups(true, 3, 1), export.OpticsGroups);
            // Beam shifts read once are kept with the movies.
            Assert.Equal(.1, session.Movies[1].BeamShift!.X); Assert.Null(session.Movies[3].BeamShift);
            Assert.Contains(WarpExportJob.Describe(export, 1, new(4, 0, null, null, null, null, [])),
                line => line == "Optics groups: 3 by EPU beam shift, each within 0.05 of its group's mean; noEpuMetadata holds the 1 micrographs without EPU metadata.");

            // An export of part of the session numbers its groups 1..n but names them as the whole session does.
            session.SetDisposition([0, 2], WarpDisposition.Exclude);
            var part = await session.ExportAsync(false, Path.Combine(root, "part"), root);
            Assert.Equal(["beamShift2", "noEpuMetadata"], new Star(part.StarPath, "optics").GetColumn("rlnOpticsGroupName"));
            Assert.Equal(["1", "2"], new Star(part.StarPath, "micrographs").GetColumn("rlnOpticsGroup"));

            // Changing only the grouping keeps every result; with grouping off there is one group.
            Assert.Equal("Retain current results; exports from now on use the new optics groups.", session.GetSettingsImpact(session.Settings with { EpuOpticsGroups = false }));
            session.Apply(session.Settings with { EpuOpticsGroups = false });
            var single = await session.ExportAsync(false, Path.Combine(root, "single"), root);
            Assert.Equal(["opticsGroup1"], new Star(single.StarPath, "optics").GetColumn("rlnOpticsGroupName"));
            Assert.Equal(new WarpOpticsGroups(false, 1, 0), single.OpticsGroups);
        }
        finally { manager.Dispose(); Directory.Delete(root, true); }
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
}
