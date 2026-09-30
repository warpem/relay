using Refund.Jobs;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Classes3D.Class3DSelect;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.Refinement.Refinement3D.Refine3D;
using Refund.Jobs.Ts.Extraction.ExtractParticles;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class RelionParticleInputTests
{
    private const string Root = "/tmp/relay-particle-input-tests";

    [Theory]
    [InlineData(ParticleType.Image)]
    [InlineData(ParticleType.Tomogram)]
    [InlineData(ParticleType.Tiltseries)]
    public void RelionCommands_SelectInputByParticleRepresentation(ParticleType type)
    {
        RelionJob[] jobs = [new Class3D(), new Class3D { UseWorkerPool = true },
            new Refine3D(), new Refine3D { UseWorkerPool = true }, new InitialReference()];
        foreach (var job in jobs)
        {
            ConnectInputs(job, type);
            var args = job.ComposeCommandArguments();
            bool stacks = type == ParticleType.Tiltseries;
            Assert.Equal(stacks ? "input/optimisation_set.star" : "input/particles.star",
                args[stacks ? "ios" : "i"]);
            Assert.False(args.ContainsKey(stacks ? "i" : "ios"));
        }
    }

    [Theory]
    [InlineData(ExportType.Subtomograms, ParticleType.Tomogram)]
    [InlineData(ExportType.Tiltseries, ParticleType.Tiltseries)]
    public void Extraction_AdvertisesOnlyFilesWrittenByWarp(ExportType export, ParticleType type)
    {
        var job = new ExtractParticles { OutputType = export };
        ConnectInputs(job, type);
        var particles = ParticleOutput(job);
        Assert.Equal(type, particles.DataDimensionality);
        Assert.Equal(Path.Combine(job.DirectoryPath, "particles.star"), particles.ParticlesSingleStarPath);
        if (export == ExportType.Tiltseries)
        {
            Assert.Equal(Path.Combine(job.DirectoryPath, "particles_tomograms.star"), particles.TomogramsStarPath);
            Assert.Equal(Path.Combine(job.DirectoryPath, "particles_optimisation_set.star"), particles.OptimisationSetStarPath);
        }
        else
        {
            Assert.Null(particles.TomogramsStarPath);
            Assert.Null(particles.OptimisationSetStarPath);
        }
    }

    [Theory]
    [InlineData(ParticleType.Image)]
    [InlineData(ParticleType.Tomogram)]
    [InlineData(ParticleType.Tiltseries)]
    public void RelionOutputs_OnlyAdvertiseOptimisationSetsForTiltStacks(ParticleType type)
    {
        Job[] jobs = [new Class3D(), new Class3DContinue(), new Class3DSelect(),
            new Refine3D(), new InitialReference()];
        foreach (var job in jobs)
        {
            ConnectInputs(job, type);
            var particles = ParticleOutput(job);
            Assert.Equal(type, particles.DataDimensionality);
            if (type == ParticleType.Tiltseries)
                Assert.StartsWith(job.DirectoryPath + "/", particles.OptimisationSetStarPath);
            else
                Assert.Null(particles.OptimisationSetStarPath);
        }
    }

    private static ParticleSet ParticleOutput(Job job) => Assert.IsType<ParticleSet>(
        job.PortsOut.Values.First(p => p.ResourceType == typeof(ParticleSet)).GetResource());

    private static void ConnectInputs(Job job, ParticleType type)
    {
        JobRegistry.EnsurePopulated();
        job.Id = 424;
        job.Space = new Space { RootDirectory = Root };
        if (job is Class3DContinue)
        {
            var previous = new Class3D();
            ConnectInputs(previous, type);
            var input = job.PortsIn[Class3DContinue.PortInOptimizer];
            var source = previous.PortsOut.Values.Single(p => p.ResourceType == typeof(ContinuableClass3D));
            input.Edges.Add(new Edge { Source = source, Target = input });
        }
        foreach (var input in job.PortsIn.Values)
        {
            if (input.ResourceType == typeof(ParticleSet))
                Connect(input, () => new ParticleSet
                {
                    HasData = true,
                    DataDimensionality = type,
                    ParticlesSingleStarPath = Root + "/input/particles.star",
                    // Include stale paths to ensure representation, not path presence, decides routing.
                    OptimisationSetStarPath = Root + "/input/optimisation_set.star",
                    TomogramsStarPath = Root + "/input/tomograms.star"
                });
            else if (input.ResourceType == typeof(MapList))
                Connect(input, () => new MapList([new Map(averageVolumePath: Root + "/map.mrc")]));
        }
    }

    private static void Connect<T>(PortIn input, Func<T> resource) where T : Resource
    {
        var source = new PortOut(input.Job, typeof(T), "source", "Source", _ => resource());
        input.Edges.Add(new Edge { Source = source, Target = input });
    }
}
