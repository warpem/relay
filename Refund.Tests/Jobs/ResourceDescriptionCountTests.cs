using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Common.Tools.ExpandSymmetry;
using Refund.Jobs.Common.Tools.ThresholdStatistics;
using Refund.Jobs.Fs.Extraction.ExtractParticles2D;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Jobs.Refinement.Classes2D.Class2DSelect;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Classes3D.Class3DSelect;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.Refinement.Refinement3D.Refine3D;
using Refund.Jobs.Ts.Extraction.ExtractParticles;
using Refund.Jobs.Ts.Selection.SelectParticles;
using Refund.Jobs.Ts.Selection.SelectTomograms;

namespace Refund.Tests.Jobs;

public class ResourceDescriptionCountTests
{
    [Fact]
    public void ClassDescriptions_ReportConfiguredClassCountsWithoutReadingFiles()
    {
        var classification = Materialize(new Class2D { NClasses = 73 });
        var templates = classification.PortsOut["Templates"].GetResource();
        Assert.Equal(73, templates.ItemCount);

        var selection = Materialize(new Class2DSelect
        {
            SelectedClasses = [1, 4, 7],
            UnselectedClasses = []
        });
        var selected = selection.PortsOut.Values
            .Where(port => port.ResourceType == typeof(TemplateSet))
            .Select(port => port.GetResource().ItemCount).ToArray();
        Assert.Contains(3L, selected);
        Assert.Contains(0L, selected);
    }

    [Fact]
    public void RewrittenParticleOutputs_DoNotInheritTheInputCount()
    {
        Job[] jobs =
        [
            new Class2D(), new Class2DSelect(), new Class3D(), new Class3DSelect(),
            new InitialReference(), new Refine3D(), new ExpandSymmetry(),
            new ThresholdStatistics(), new ExtractParticles2D(), new ExtractParticles(),
            new SelectParticles()
        ];

        foreach (var job in jobs)
        {
            Materialize(job);
            foreach (var input in job.PortsIn.Values.Where(port => port.ResourceType == typeof(ParticleSet)))
                Connect(input, () => new ParticleSet
                {
                    ParticlesSingleStarPath = "/input/particles.star",
                    ItemCount = 987
                });
            foreach (var input in job.PortsIn.Values.Where(port => port.ResourceType == typeof(TomogramSet)))
                Connect(input, () => new TomogramSet { TiltSeriesSet = new TiltSeriesSet { HasMetadata = true } });

            var outputs = job.PortsOut.Values.Where(port => port.ResourceType == typeof(ParticleSet)).ToArray();
            Assert.NotEmpty(outputs);
            foreach (var output in outputs)
            {
                var particles = Assert.IsType<ParticleSet>(output.GetResource());
                Assert.Null(particles.ItemCount);
            }
        }
    }

    [Fact]
    public void MicrographOutput_CountsSuccessfulItemsAndPreservesUnknown()
    {
        var job = Materialize(new MotionAndCTF2D());
        Connect(job.PortsIn[MotionAndCTF2D.PortInDataSet], () => new DataSetFs { ItemCount = 100 });
        var output = job.PortsOut.Values.Single();

        Assert.Null(output.GetResource().ItemCount);
        job.NItemsProcessed = 10;
        job.NItemsFailed = 3;
        Assert.Equal(7, output.GetResource().ItemCount);
        job.NItemsFailed = 10;
        Assert.Equal(0, output.GetResource().ItemCount);
    }

    [Fact]
    public void PassThroughTomograms_PreserveIncomingCount()
    {
        var job = Materialize(new SelectParticles());
        Connect(job.PortsIn[SelectParticles.PortInTomogramSet], () => new TomogramSet
        {
            ItemCount = 42,
            TiltSeriesSet = new TiltSeriesSet { HasMetadata = true }
        });

        Assert.Equal(42, job.PortsOut[SelectParticles.PortOutTomogramSet].GetResource().ItemCount);
    }

    [Fact]
    public void SelectingNoTomograms_WritesAnEmptyOutputList()
    {
        var root = Path.Combine(Path.GetTempPath(), "relay-count-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inputPath = Path.Combine(root, "input.json");
            File.WriteAllText(inputPath, "[{\"Path\":\"excluded.tomostar\"}]");
            var job = Materialize(new SelectTomograms
            {
                IsInteractiveFinished = true,
                DeselectedTomograms = ["excluded"]
            });
            job.Space.RootDirectory = root;
            Directory.CreateDirectory(job.DirectoryPath);
            File.Copy(inputPath, job.ResProcessedItemsJson);
            Connect(job.PortsIn[SelectTomograms.PortInTomogramSet], () => new TomogramSet
            {
                ProcessedItemsJson = inputPath,
                TiltSeriesSet = new TiltSeriesSet()
            });

            job.RunLocal(CancellationToken.None);

            Assert.Equal("[]", File.ReadAllText(job.ResProcessedItemsJson));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static T Materialize<T>(T job) where T : Job
    {
        job.Id = 1;
        job.Space = new Space { RootDirectory = "/unreadable-resource-count-test" };
        return job;
    }

    private static void Connect<T>(PortIn input, Func<T> resource) where T : Resource
    {
        var source = new PortOut(input.Job, typeof(T), "source", "Source", _ => resource());
        input.Edges.Add(new Edge { Source = source, Target = input });
    }
}
