using System.Text.Json;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes2D.BeamClass2D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class BeamClass2DTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay beam 2d " + Guid.NewGuid().ToString("N"));
    private readonly ParticleSet _particles = new() { HasData = true, ParticlesSingleStarPath = "/particles.star", HasAngles = true };
    private BeamClass2D NewJob()
    {
        JobRegistry.EnsurePopulated();
        var job = new BeamClass2D { Id = 1, Space = new Space { RootDirectory = _root } };
        var input = job.PortsIn["Particles"];
        input.Edges.Add(new Edge { Target = input, Source = new PortOut(job, typeof(ParticleSet), "source", "Source", _ => _particles) });
        return job;
    }

    [Fact]
    public void CommandUsesClass2DOptionsAndConditionalDenoising()
    {
        var job = NewJob();
        Assert.Equal("beam class2d", job.CommandName);
        Assert.Equal(new[] { "Particles" }, job.PortsIn.Keys);
        Assert.Empty(job.ValidatePortInputs());
        var args = job.ComposeCommandArguments();
        Assert.Equal("64", args["final-classes"]);
        Assert.Equal("60", args["iterations"]);
        Assert.Equal("64", args["working-box"]);
        Assert.Equal("8", args["workers"]);
        Assert.Equal("soft", args["reconstruction-assignments"]);
        Assert.Equal(8, job.CoreCount);
        Assert.Contains("beam", job.RequiredModules);
        Assert.DoesNotContain("denoise", args.Keys);
        Assert.DoesNotContain("denoise-initial-steps", args.Keys);
        Assert.Contains("show-progress", args.Keys);
        Assert.DoesNotContain("reference", args.Keys);
        job.Regularization = Beam2DRegularization.Denoise;
        job.Device = BeamDevice.CPU;
        job.ReconstructionAssignments = Beam2DAssignments.Hard;
        args = job.ComposeCommandArguments();
        Assert.Contains("denoise", args.Keys);
        Assert.Equal("10000", args["denoise-initial-steps"]);
        Assert.Equal("cpu", args["device"]);
        Assert.Equal("hard", args["reconstruction-assignments"]);
        Assert.Equal(0, job.GpuCount);
    }

    [Theory]
    [InlineData("--workers 32")]
    [InlineData("--output /elsewhere")]
    [InlineData("--quiet")]
    [InlineData("--dont-show-progress")]
    [InlineData("--dry-run")]
    public void CustomArgumentsCannotBreakJobCollection(string custom)
    {
        var job = NewJob(); job.AdditionalArguments = custom;
        Assert.Contains(nameof(job.AdditionalArguments), job.ValidateInputs().Keys);
    }

    [Fact]
    public void RejectsTiltStacksAndInvalidPcaAndBoxSettings()
    {
        var job = NewJob();
        _particles.DataDimensionality = ParticleType.Tiltseries;
        Assert.Contains("Particles", job.ValidatePortInputs().Keys);
        job.WorkingBox = 17; job.InitialClasses = 65; job.MaximumPcaParticles = 16;
        var errors = job.ValidateInputs();
        Assert.Contains(nameof(job.WorkingBox), errors.Keys);
        Assert.Contains(nameof(job.InitialClasses), errors.Keys);
        Assert.Contains(nameof(job.MaximumPcaParticles), errors.Keys);
    }

    [Fact]
    public void PartialSnapshotsCannotPublishParticlesAndFinalSnapshotUsesActualClassCount()
    {
        var job = NewJob();
        Write(Path.Combine(job.SnapshotDirectory(1), "snapshot.json"), "{}");
        Assert.Empty(job.CommittedIterations());
        Snapshot(job, 1, 4, false);
        job.TrackProgressResults()!();
        Assert.Equal(1, job.VisAvailableIteration);
        Assert.Null(job.PortsOut["Particles"].GetResource());
        Assert.Equal(4L, job.PortsOut["Templates"].GetResource().ItemCount);
        Snapshot(job, 2, 7, true);
        Write(Path.Combine(job.BeamDirectory, "particles.star"), "data_particles");
        Write(job.PathStdOut, "setup\n=== Snapshot 1 ===\nold\rnew\nPublishing snapshot 1: batch 10\n=== Snapshot 2 ===\nFinal assignment progress\nPublishing snapshot 2: batch 20; final assignments available\nfinished");
        Write(job.PathStdErr, "warning");
        job.FinalizeRun((_, action) => action(job));
        Assert.Equal(2, job.VisAvailableIteration);
        Assert.Equal(2, job.FinalAssignmentIteration);
        Assert.False(job.CanExport(1)); Assert.True(job.CanExport(2));
        var output = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource());
        Assert.True(output.HasClasses); Assert.True(output.HasShifts); Assert.False(output.HasAngles);
        Assert.Equal(Path.Combine(job.BeamDirectory, "particles.star"), output.ParticlesSingleStarPath);
        Assert.Equal("/particles.star", _particles.ParticlesSingleStarPath);
        Assert.Equal(7L, job.PortsOut["Templates"].GetResource().ItemCount);
        Assert.Contains("new", File.ReadAllText(job.LogFilePath(1)));
        Assert.Contains("Final assignment progress", File.ReadAllText(job.LogFilePath(2)));
        Assert.DoesNotContain("old", File.ReadAllText(job.LogFilePath(1)));
        Assert.Equal("warning", File.ReadAllText(job.ErrorFilePath));
        var clone = Assert.IsType<BeamClass2D>(Job.CreateFromPolymorphicJson(Job.ToPolymorphicJson(job), job.Space, new List<User>().AsReadOnly()));
        Assert.Equal(2, clone.FinalAssignmentIteration);
        Assert.True(clone.CanExport(2));
    }

    [Fact]
    public void EachFullDataPassAdvancesVisualizationsAndKeepsItsOwnLog()
    {
        var job = NewJob();
        Snapshot(job, 1, 4, false);
        job.TrackProgressResults()!();
        Snapshot(job, 2, 4, false);
        job.TrackProgressResults()!();
        Assert.Equal(2, job.VisAvailableIteration);
        Assert.Null(job.PortsOut["Particles"].GetResource());
        Snapshot(job, 3, 4, true);
        job.TrackProgressResults()!();
        Assert.Equal(3, job.VisAvailableIteration);
        Assert.False(job.CanExport(3)); // Final STAR is not published yet.
        Write(Path.Combine(job.BeamDirectory, "particles.star"), "data_particles");
        job.TrackProgressResults()!();
        Assert.True(job.CanExport(3));
        Write(job.PathStdOut, "=== Snapshot 2 ===\nFull-data pass 1 / 2\nPublishing snapshot 2: full-data pass 1 / 2\n=== Snapshot 3 ===\nFull-data pass 2 / 2\nPublishing snapshot 3: full-data pass 2 / 2");
        job.TrackProgressLogs()!();
        Assert.Contains("Full-data pass 1 / 2", File.ReadAllText(job.LogFilePath(2)));
        Assert.DoesNotContain("Full-data pass 2 / 2", File.ReadAllText(job.LogFilePath(2)));
        Assert.Contains("Full-data pass 2 / 2", File.ReadAllText(job.LogFilePath(3)));
    }

    private static void Snapshot(BeamClass2D job, int i, int count, bool final)
    {
        Write(Path.Combine(job.SnapshotDirectory(i), "snapshot.json"), JsonSerializer.Serialize(new
        { class_count = count, principal_component_count = 2, embedding_particle_count = 20, final }));
        foreach (var file in new[] { "classes.mrcs", "model.star", "principal_components.mrcs", "embeddings.npz" })
            Write(Path.Combine(job.SnapshotDirectory(i), file), "complete");
        foreach (var path in new[] { job.VisClassAtlas(i), job.VisClassStats(i), job.VisPcaAtlas(i), job.VisEmbeddings(i), job.VisPcaInfo(i), job.VisCard(i) })
            Write(path, path == job.VisClassStats(i) ? JsonSerializer.Serialize(Enumerable.Range(1, count).Select(c => new { Id = c })) : "saved");
    }
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
