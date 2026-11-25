using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Common.Import.ImportParticlePositions;
using Warp;

namespace Refund.Tests.Jobs;

/// <summary>
/// Covers deriving the per-series STAR file name suffix from a WarpTools template-matching
/// output directory, and turning a tilt series name into the path of its STAR file.
/// </summary>
[Collection("JobRegistry")]
public class ImportParticlePositionsTests
{
    private static readonly object PopulateLock = new();

    private static void EnsurePopulated()
    {
        lock (PopulateLock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    private static ImportParticlePositions MakeJob(string resolvedSuffix)
    {
        EnsurePopulated();

        return new ImportParticlePositions
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            Id = 1,
            InputType = InputTypes.MultipleFiles,
            MultipleFilesDirectory = "/data/matching",
            ResolvedFilesSuffix = resolvedSuffix
        };
    }

    private static ParticleSet OutputParticles(ImportParticlePositions job)
    {
        var particles = job.PortsOut[ImportParticlePositions.PortInParticles].GetResource() as ParticleSet;
        Assert.NotNull(particles);

        return particles;
    }

    #region Suffix detection

    [Fact]
    public void DetectSuffix_UniformWarpToolsOutput_ResolvesSuffix()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_10.00Apx_ribosome.star",
            "TS_02_10.00Apx_ribosome.star",
            "TS_03_10.00Apx_ribosome.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_10.00Apx_ribosome", result.Suffix);
        Assert.Equal(3, result.MatchedCount);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void DetectSuffix_AwkwardSeriesNames_StillResolves()
    {
        // Acquisition software emits names with underscores, dots, spaces and digit runs.
        // The series name is never parsed - only what follows the pixel-size anchor matters.
        var result = ImportParticlePositions.DetectSuffix([
            "Position_1_2 [15.3].star_backup_8.00Apx_apoF.star",
            "grid3.lamella-04_8.00Apx_apoF.star",
            "20240517 sample A_8.00Apx_apoF.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_8.00Apx_apoF", result.Suffix);
        Assert.Equal(3, result.MatchedCount);
    }

    [Fact]
    public void DetectSuffix_SeriesNameContainsAnchor_LastAnchorWins()
    {
        // A series that was itself named after an earlier matching run.
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_5.00Apx_old_10.00Apx_ribosome.star",
            "TS_02_5.00Apx_old_10.00Apx_ribosome.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_10.00Apx_ribosome", result.Suffix);
    }

    [Fact]
    public void DetectSuffix_CommaDecimalSeparator_IsRecognized()
    {
        // WarpTools formats the pixel size with the ambient culture, so the separator varies.
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_10,00Apx_ribosome.star",
            "TS_02_10,00Apx_ribosome.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_10,00Apx_ribosome", result.Suffix);
    }

    [Fact]
    public void DetectSuffix_StrayFileAlongsideCleanGroup_ResolvesAndReportsStray()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_10.00Apx_ribosome.star",
            "TS_02_10.00Apx_ribosome.star",
            "notes.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_10.00Apx_ribosome", result.Suffix);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(["notes.star"], result.Unmatched);
    }

    [Fact]
    public void DetectSuffix_TwoTemplates_IsAmbiguousAndListsCandidates()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_10.00Apx_ribosome.star",
            "TS_02_10.00Apx_ribosome.star",
            "TS_01_10.00Apx_proteasome.star"
        ]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Suffix);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(2, result.Candidates["_10.00Apx_ribosome"]);
        Assert.Equal(1, result.Candidates["_10.00Apx_proteasome"]);
    }

    [Fact]
    public void DetectSuffix_TwoBinnings_IsAmbiguous()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_10.00Apx_ribosome.star",
            "TS_01_5.00Apx_ribosome.star"
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void DetectSuffix_NoRecognizableNames_FailsAndReportsAll()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "TS_01_particles.star",
            "TS_02_particles.star"
        ]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Candidates);
        Assert.Equal(2, result.Unmatched.Count);
    }

    [Fact]
    public void DetectSuffix_EmptyListing_Fails()
    {
        var result = ImportParticlePositions.DetectSuffix([]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void DetectSuffix_IgnoresDirectoryComponentOfPaths()
    {
        var result = ImportParticlePositions.DetectSuffix([
            "/data/matching/TS_01_10.00Apx_ribosome.star",
            "/data/matching/TS_02_10.00Apx_ribosome.star"
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("_10.00Apx_ribosome", result.Suffix);
    }

    #endregion

    #region Series name to STAR path

    [Fact]
    public void ToMultiStarPath_StripsTomostarExtension()
    {
        var job = MakeJob("_10.00Apx_ribosome");
        var particles = OutputParticles(job);

        // Consumers pass the tomostar file name from processed_items.json, not a bare series name.
        Assert.Equal(Path.Combine("/tmp/relay-test", "1", "TS_01_10.00Apx_ribosome.star"),
                     particles.ToMultiStarPath("TS_01.tomostar"));
    }

    [Fact]
    public void ToMultiStarPath_StripsDirectoryComponent()
    {
        var job = MakeJob("_10.00Apx_ribosome");
        var particles = OutputParticles(job);

        Assert.Equal(Path.Combine("/tmp/relay-test", "1", "TS_01_10.00Apx_ribosome.star"),
                     particles.ToMultiStarPath("tiltseries/TS_01.tomostar"));
    }

    [Fact]
    public void ToMultiStarPath_KeepsDotsWithinSeriesName()
    {
        var job = MakeJob("_8.00Apx_apoF");
        var particles = OutputParticles(job);

        Assert.Equal(Path.Combine("/tmp/relay-test", "1", "grid3.lamella-04_8.00Apx_apoF.star"),
                     particles.ToMultiStarPath("grid3.lamella-04.tomostar"));
    }

    [Fact]
    public void GetParticles_SingleFileMode_HasNoMultiStarPath()
    {
        EnsurePopulated();

        var job = new ImportParticlePositions
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            Id = 1,
            InputType = InputTypes.SingleFile,
            SingleFilePath = "/data/particles.star"
        };

        var particles = OutputParticles(job);

        Assert.Null(particles.ToMultiStarPath);
        Assert.True(particles.IsSingleStar);
    }

    #endregion

    #region Pixel size without an optics table

    private static Star Particles(params (string Name, string[] Values)[] columns) =>
        new(columns.Select(c => c.Values).ToArray(), columns.Select(c => c.Name).ToArray());

    [Fact]
    public void DetectPixelSize_PytomStyleTable_UsesDetectorPixelSize()
    {
        var table = Particles(("rlnCoordinateX", ["1", "2"]), ("rlnDetectorPixelSize", ["11.328000", "11.328000"]));

        Assert.Equal(11.328M, ImportParticlePositions.DetectPixelSize(table, null));
    }

    [Fact]
    public void DetectPixelSize_OpticsTableTakesPrecedence()
    {
        var table = Particles(("rlnCoordinateX", ["1"]), ("rlnDetectorPixelSize", ["11.328"]));
        var optics = Particles(("rlnOpticsGroup", ["1"]), ("rlnImagePixelSize", ["2.5"]));

        Assert.Equal(2.5M, ImportParticlePositions.DetectPixelSize(table, optics));
    }

    [Fact]
    public void DetectPixelSize_DetectorPixelSizeWithMagnification_Scales()
    {
        var table = Particles(("rlnDetectorPixelSize", ["5"]), ("rlnMagnification", ["50000"]));

        Assert.Equal(1M, ImportParticlePositions.DetectPixelSize(table, null));
    }

    [Fact]
    public void DetectPixelSize_DisagreeingOrMissing_ReturnsNull()
    {
        Assert.Null(ImportParticlePositions.DetectPixelSize(
                        Particles(("rlnDetectorPixelSize", ["1", "2"])), null));
        Assert.Null(ImportParticlePositions.DetectPixelSize(
                        Particles(("rlnCoordinateX", ["1"])), null));
    }

    [Fact]
    public void RunLocal_NamedParticleBlockWithoutOptics_Imports()
    {
        EnsurePopulated();

        string root = Path.Combine(Path.GetTempPath(), "relay-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string star = Path.Combine(root, "pytom.star");
            File.WriteAllText(star,
                "# Created by the starfile Python package\n\ndata_particles\n\nloop_\n" +
                "_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnCoordinateZ #3\n_rlnAnglePsi #4\n" +
                "_rlnDetectorPixelSize #5\n_rlnMicrographName #6\n" +
                "367.000000\t293.000000\t188.000000\t-60.000000\t11.328000\tts_01.tomostar\n" +
                "239.000000\t258.000000\t189.000000\t0.000000\t11.328000\tts_01.tomostar\n");

            var job = new ImportParticlePositions
            {
                Space = new Space { RootDirectory = root },
                Id = 1,
                InputType = InputTypes.SingleFile,
                SingleFilePath = star
            };

            job.RunLocal(CancellationToken.None);

            var particles = OutputParticles(job);
            Assert.True(particles.HasPositions);
            Assert.True(particles.Has3dCoords);
            Assert.True(particles.HasAngles);
            Assert.Equal(11.328M, particles.CoordPixelSize);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    #endregion
}
