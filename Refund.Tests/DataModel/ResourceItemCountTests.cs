using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.DataModel;

[Collection("JobRegistry")]
public class ResourceItemCountTests
{
    public ResourceItemCountTests()
    {
        if (Job.Types.Count == 0)
            Job.PopulateStatic();
    }

    [Fact]
    public void ResourceDescriptions_DistinguishUnknownZeroAndKnownCounts()
    {
        Assert.Null(new ParticleSet().ItemCount);
        Assert.Equal(0L, new ParticleSet { ParticleCount = 0 }.ItemCount);
        Assert.Equal(23L, new ParticleSet { ParticleCount = 23 }.ItemCount);
        Assert.Equal(0L, new MapList([]).ItemCount);
        Assert.Equal(2L, new MapList([new Map(), new Map()]).ItemCount);
        Assert.Equal(12L, new TemplateSet("missing.star", "missing.mrcs", null, 12).ItemCount);
        Assert.Null(new Mask("missing.mrc").ItemCount);
    }

    [Fact]
    public void OutputCounts_SurviveSerializationAndAreClearable()
    {
        var job = new Note { OutputItemCounts = new() { ["Particles"] = 3_000_000_000L, ["Empty"] = 0 } };
        var restored = new Note();
        restored.ReadFromJson(job.ToJson());

        Assert.Equal(3_000_000_000L, restored.OutputItemCounts["Particles"]);
        Assert.Equal(0L, restored.OutputItemCounts["Empty"]);

        restored.ClearProperties();
        Assert.Empty(restored.OutputItemCounts);
        Assert.Equal(2, job.OutputItemCounts.Count);
    }

    [Fact]
    public void OldJobs_HaveUnknownCountsWithoutOpeningOutputFiles()
    {
        var job = new Note();
        var json = job.ToJson();
        json.AsObject().Remove(nameof(Job.OutputItemCounts));
        job.ReadFromJson(json);
        var port = new PortOut(job, typeof(ParticleSet), "Particles", "Particles",
            _ => new ParticleSet { ParticlesSingleStarPath = "/nonexistent/huge.star" });

        Assert.Empty(job.OutputItemCounts);
        Assert.Null(port.ItemCount);
        Assert.Null(port.AsReadOnly().ItemCount);
        Assert.Null(port.GetResource().ItemCount);
    }

    [Fact]
    public void SavedCount_DoesNotConstructResourceForLabels()
    {
        var job = new Note { OutputItemCounts = new() { ["Particles"] = 0 } };
        var port = new PortOut(job, typeof(ParticleSet), "Particles", "Particles",
            _ => throw new Exception("Label must use saved metadata"));

        Assert.Equal(0L, port.ItemCount);
        Assert.Equal(0L, port.AsReadOnly().ItemCount);
    }

    [Fact]
    public void SavedCounts_FlowThroughResourcesWithoutBeingAppliedToOlderIterations()
    {
        var job = new Note { OutputItemCounts = new() { ["Particles"] = 42 } };
        var port = new PortOut(job, typeof(ParticleSet), "Particles", "Particles", _ => new ParticleSet());
        var downstream = new PortOut(new Note(), typeof(ParticleSet), "Particles", "Particles", _ => port.GetResource());

        Assert.Equal(42L, port.GetResource().ItemCount);
        Assert.Equal(42L, downstream.ItemCount);
        Assert.Null(port.GetResource(0).ItemCount);
        Assert.Null(port.GetResourceDescription().ItemCount);
    }

    [Fact]
    public void BlueprintLabel_IsSafeBeforeResourcePathsCanBeResolved()
    {
        var blueprint = Job.CreateBlueprint(typeof(Note));
        var port = new PortOut(blueprint, typeof(ParticleSet), "Particles", "Particles",
            _ => new ParticleSet { ParticlesSingleStarPath = Path.Combine(blueprint.DirectoryPath, "particles.star") });

        Assert.Null(port.ItemCount);
    }

    [Fact]
    public void NewlyCreatedJobs_CanDisplayOutputLabelsBeforeInputsAreConnected()
    {
        foreach (Type type in Job.Types.Values)
        {
            var job = (Job)Activator.CreateInstance(type)!;
            job.Id = 1;
            job.Space = new Space { RootDirectory = "/nonexistent/relay-count-labels" };
            foreach (var port in job.PortsOut.Values)
            {
                var exception = Record.Exception(() => { _ = port.ItemCount; });
                Assert.True(exception == null, $"{type.Name}.{port.Name}: {exception}");
            }
        }
    }
}
