using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Common.Notes.Note;
using Refund.Utils;

namespace Refund.Tests.Utils;

public class ResourceItemCounterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay-resource-count-" + Guid.NewGuid().ToString("N"));

    public ResourceItemCounterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ReadsActualParticleOutputInsteadOfInheritedOrPreviouslySavedCounts()
    {
        var particles = new ParticleSet { ParticlesSingleStarPath = WriteStar("particles.star", 3), ItemCount = 999 };
        var job = new ResourceJob(particles);
        job.OutputItemCounts["Output0"] = 888;

        var counts = ResourceItemCounter.CountOutputs(job);

        Assert.Equal(3, counts["Output0"]);
        Assert.Equal(888, job.OutputItemCounts["Output0"]);
    }

    [Fact]
    public void MissingOrMalformedParticleOutputRemainsUnknown()
    {
        var missing = new ParticleSet { ParticlesSingleStarPath = Path.Combine(_directory, "missing.star"), ItemCount = 999 };
        var malformed = new ParticleSet { ParticlesSingleStarPath = Write("malformed.star", "data_particles\nloop_\n_rlnImageName\n_rlnOpticsGroup\n1@p") };

        Assert.Empty(ResourceItemCounter.CountOutputs(new ResourceJob(missing, malformed)));
    }

    [Fact]
    public void CountsOnlyMappedProcessedParticleFilesAndDeduplicatesPaths()
    {
        WriteStar("a_selected.star", 2);
        WriteStar("b_selected.star", 3);
        WriteStar("a_other_template.star", 100);
        var particles = new ParticleSet
        {
            ParticlesMultiStarDirectory = _directory,
            ToMultiStarPath = path => Path.Combine(_directory, Path.GetFileNameWithoutExtension(path) + "_selected.star"),
            PickedInTomograms = new TomogramSet
            {
                ProcessedItemsJson = Write("processed.json", """[{"Path":"a.tomostar"},{"Path":"b.tomostar"},{"Path":"a.tomostar"}]""")
            }
        };

        Assert.Equal(5, ResourceItemCounter.CountOutputs(new ResourceJob(particles))["Output0"]);
    }

    [Fact]
    public void MissingPerImageFilesContributeZeroToSparseParticleSets()
    {
        WriteStar("a.star", 2);
        var particles = new ParticleSet
        {
            ParticlesMultiStarDirectory = _directory,
            ToMultiStarPath = path => Path.Combine(_directory, Path.GetFileNameWithoutExtension(path) + ".star"),
            PickedInMicrographs = new MicrographSet
            {
                ProcessedItemsJson = Write("processed.json", """[{"Path":"a.mrc"},{"Path":"missing.mrc"}]""")
            }
        };

        Assert.Equal(2, ResourceItemCounter.CountOutputs(new ResourceJob(particles))["Output0"]);
    }

    [Fact]
    public void ImportedMultistarSetUsesMappedSuffixToExcludeOtherFiles()
    {
        WriteStar("a_10.00Apx_target.star", 2);
        WriteStar("b_10.00Apx_target.star", 3);
        WriteStar("a_10.00Apx_other.star", 100);
        Write("optics.star", "data_optics\nloop_\n_rlnOpticsGroup\n1\n");
        var particles = new ParticleSet
        {
            ParticlesMultiStarDirectory = _directory,
            ToMultiStarPath = path => Path.Combine(_directory, Path.GetFileNameWithoutExtension(path) + "_10.00Apx_target.star")
        };

        Assert.Equal(5, ResourceItemCounter.CountOutputs(new ResourceJob(particles))["Output0"]);
    }

    [Fact]
    public void ExistingEmptyParticleDirectoryHasKnownZeroCount()
    {
        var particles = new ParticleSet { ParticlesMultiStarDirectory = _directory };

        Assert.Equal(0, ResourceItemCounter.CountOutputs(new ResourceJob(particles))["Output0"]);
    }

    [Fact]
    public void CountsProcessedCollectionsFromTheirOwnMetadata()
    {
        string json = Write("processed.json", """[{"Path":"one"},{"Path":"two"}]""");
        var job = new ResourceJob(
            new MicrographSet { ProcessedItemsJson = json, ItemCount = 100 },
            new TiltSeriesSet { ProcessedItemsJson = json, ItemCount = 100 },
            new TomogramSet { ProcessedItemsJson = json, ItemCount = 100 });

        var counts = ResourceItemCounter.CountOutputs(job);

        Assert.Equal(3, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public void MalformedCollectionMetadataDoesNotBecomeZero()
    {
        var job = new ResourceJob(new MicrographSet { ProcessedItemsJson = Write("invalid.json", "[{},") });

        Assert.Empty(ResourceItemCounter.CountOutputs(job));
    }

    [Fact]
    public void DataSetsRespectTheirFilePatternAndRecursion()
    {
        Write("a.eer", "");
        Write("sub/b.eer", "");
        Write("c.mrc", "");
        Write("a.tomostar", "");
        Write("sub/b.tomostar", "");
        var job = new ResourceJob(
            new DataSetFs { DataDirectory = _directory, FileSearchPattern = "*.eer", DoRecursiveSearch = true },
            new DataSetFs { DataDirectory = _directory, FileSearchPattern = "*.eer", DoRecursiveSearch = false },
            new DataSetTs { DataDirectory = _directory });

        var counts = ResourceItemCounter.CountOutputs(job);

        Assert.Equal(2, counts["Output0"]);
        Assert.Equal(1, counts["Output1"]);
        Assert.Equal(1, counts["Output2"]);
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ResourceItemCounter.CountOutputs(new ResourceJob(), cancellation.Token));
    }

    private string WriteStar(string name, int count) => Write(name,
        "data_particles\nloop_\n_rlnImageName\n" + string.Concat(Enumerable.Repeat("1@particles.mrcs\n", count)));

    private string Write(string name, string contents)
    {
        string path = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class ResourceJob : Note
    {
        public ResourceJob(params Resource[] resources)
        {
            PortsOut = new(resources.Select((resource, i) =>
                new PortOut(this, resource.GetType(), $"Output{i}", $"Output{i}", _ => resource))
                .ToDictionary(port => port.Name));
        }
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
