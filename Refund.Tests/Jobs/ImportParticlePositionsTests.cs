using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Common.Import.ImportParticlePositions;

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
            DirectoryName = "J1_ImportPositions",
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
        Assert.Equal(Path.Combine("/tmp/relay-test", "J1_ImportPositions", "TS_01_10.00Apx_ribosome.star"),
                     particles.ToMultiStarPath("TS_01.tomostar"));
    }

    [Fact]
    public void ToMultiStarPath_StripsDirectoryComponent()
    {
        var job = MakeJob("_10.00Apx_ribosome");
        var particles = OutputParticles(job);

        Assert.Equal(Path.Combine("/tmp/relay-test", "J1_ImportPositions", "TS_01_10.00Apx_ribosome.star"),
                     particles.ToMultiStarPath("tiltseries/TS_01.tomostar"));
    }

    [Fact]
    public void ToMultiStarPath_KeepsDotsWithinSeriesName()
    {
        var job = MakeJob("_8.00Apx_apoF");
        var particles = OutputParticles(job);

        Assert.Equal(Path.Combine("/tmp/relay-test", "J1_ImportPositions", "grid3.lamella-04_8.00Apx_apoF.star"),
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
            DirectoryName = "J1_ImportPositions",
            InputType = InputTypes.SingleFile,
            SingleFilePath = "/data/particles.star"
        };

        var particles = OutputParticles(job);

        Assert.Null(particles.ToMultiStarPath);
        Assert.True(particles.IsSingleStar);
    }

    #endregion
}
