using Refund.DataModel;
using Refund.Jobs.Common.Import.ImportParticles;
using Refund.Utils;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class ImportParticlesTests
{
    [Fact]
    public void CryoSparcFieldsAreConditionalAndInputPathsRetainSpaces()
    {
        JobRegistry.EnsurePopulated();
        var job = new ImportParticles
        {
            FilePath = "/data/project/J2/particles.cs",
            AdditionalMetadataPaths = "J2/pass through.cs\nJ1/locations.cs"
        };
        var property = typeof(ImportParticles).GetProperty(nameof(job.CryoSparcProjectDirectory))!;
        Assert.False(Job.IsPropertyVisible(typeof(ImportParticles), property, job.AsReadOnly()));
        job.MetadataFormat = ParticleMetadataFormat.CryoSparc;
        Assert.True(Job.IsPropertyVisible(typeof(ImportParticles), property, job.AsReadOnly()));
        Assert.Contains(nameof(job.CryoSparcProjectDirectory), job.ValidateInputs().Keys);
        job.CryoSparcProjectDirectory = "/data/project";
        Assert.Empty(job.ValidateInputs());
        Assert.Equal(new[] { job.FilePath, "J2/pass through.cs", "J1/locations.cs" }, job.CryoSparcMetadataPaths);
        var info = BeamWrapper.ImportStartInfo(job.CryoSparcMetadataPaths, job.CryoSparcProjectDirectory, "/tmp/result dir/particles.star");
        Assert.Contains("J2/pass through.cs", info.ArgumentList);
        Assert.Contains("/tmp/result dir/particles.star", info.ArgumentList);
        Assert.Contains("--absolute-paths", info.ArgumentList);
        Assert.Contains("--check-stacks", info.ArgumentList);
    }

    [Fact]
    public void RelionImportRemainsTheDefault()
    {
        var job = new ImportParticles { FilePath = "/data/particles.star" };
        Assert.Equal(ParticleMetadataFormat.Relion, job.MetadataFormat);
        Assert.Empty(job.ValidateInputs());
    }

    [Fact]
    public void MetadataDetectionPreservesPositionsAndResetsFlagsOnReimport()
    {
        var job = new ImportParticles();
        job.DetectMetadata(["rlnCoordinateX", "rlnCoordinateY", "rlnOriginXAngst", "rlnAnglePsi", "rlnDefocusU"]);
        Assert.True(job.HasPositions);
        Assert.True(job.HasShifts);
        Assert.True(job.HasAngles);
        Assert.True(job.HasCtf);
        job.DetectMetadata(["rlnImageName"]);
        Assert.False(job.HasPositions);
        Assert.False(job.HasShifts);
        Assert.False(job.HasAngles);
        Assert.False(job.HasCtf);
    }
}
