using System.Collections;
using System.Text;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Ts.Alignment.AlignMiss;
using VYaml.Serialization;
using Warp.Tools;
using MissAlignmentJob = Refund.Jobs.Ts.Alignment.AlignMiss.AlignMiss;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class MissAlignmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay missalignment " + Guid.NewGuid().ToString("N"));

    private MissAlignmentJob NewJob(int id = 1)
    {
        JobRegistry.EnsurePopulated();
        string metadata = Path.Combine(_root, "input");
        Directory.CreateDirectory(metadata);
        var job = new MissAlignmentJob { Id = id, Space = new Space { RootDirectory = _root } };
        Connect(job.PortsIn[MissAlignmentJob.PortInTiltSeriesSet], new TiltSeriesSet
        {
            HasMetadata = true,
            HasCtf = true,
            LatestMetadataDirectory = metadata,
            DataSet = new DataSetTs
            {
                TomogramDimensions = new int3(100, 100, 100),
                Micrographs = new MicrographSet { DataSetFs = new DataSetFs { PixelSize = 1.5m } }
            }
        });
        return job;
    }

    private static void Connect<T>(PortIn port, T resource) where T : Resource =>
        port.Edges.Add(new Edge { Target = port, Source = new PortOut(port.Job, typeof(T), "source", "Source", _ => resource) });

    private static Dictionary<string, Dictionary<string, object>> ReadConfig(string yaml) =>
        YamlSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(Encoding.UTF8.GetBytes(yaml));

    private static bool Visible(MissAlignmentJob job, string name) =>
        Job.IsPropertyVisible(typeof(MissAlignmentJob), typeof(MissAlignmentJob).GetProperty(name)!, job.AsReadOnly());

    [Fact]
    public void NewJobsDefaultToGeneralModelInferenceWithoutTrainingArguments()
    {
        var job = NewJob();
        Assert.False(job.DoTraining);
        Assert.True(job.UseGeneralModel);
        Assert.Equal("miss-alignment infer", job.CommandName);
        var args = job.ComposeCommandArguments();
        Assert.Equal(job.ResConfigPath, args["config-file"]);
        Assert.Equal("10", args["prepare-stacks"]);
        Assert.DoesNotContain("training-devices", args.Keys);
        Assert.DoesNotContain("reconstruction-devices", args.Keys);
        Assert.DoesNotContain("dataloaders-per-trainer", args.Keys);
        Assert.DoesNotContain("n-cluster-workers", args.Keys);
        Assert.False(job.PortsIn[MissAlignmentJob.PortInModel].IsActive());
        Assert.Empty(job.ValidatePortInputs());
        Assert.Null(job.PortsOut[MissAlignmentJob.PortOutModel].GetResource());
    }

    [Fact]
    public void TrainingFieldsAndTheirNestedOptionsAreConditional()
    {
        var job = NewJob();
        string[] training = [nameof(job.NCoarse), nameof(job.NFine), nameof(job.DoLocalDeformations),
            nameof(job.LocalModel), nameof(job.NLocal), nameof(job.BatchSize), nameof(job.WindowSize),
            nameof(job.MaxEpochs), nameof(job.LearningRate), nameof(job.NSteps), nameof(job.PerDevice), nameof(job.NWorkers)];
        foreach (bool train in new[] { false, true })
        {
            job.DoTraining = train;
            foreach (string field in training)
                Assert.Equal(train, Visible(job, field));
            Assert.Equal(!train, Visible(job, nameof(job.UseGeneralModel)));
            foreach (string field in new[] { nameof(job.AngPix), nameof(job.UseCtf), nameof(job.NGpus), nameof(job.UseWorkerPool) })
                Assert.True(Visible(job, field));
        }
        job.DoLocalDeformations = false;
        Assert.False(Visible(job, nameof(job.LocalModel)));
        Assert.False(Visible(job, nameof(job.NLocal)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ModeAndModelChoiceRoundTrip(bool train, bool general)
    {
        var job = NewJob();
        job.DoTraining = train;
        job.UseGeneralModel = general;
        var restored = Job.CreateFromPolymorphicJson(Job.ToPolymorphicJson(job), job.Space, new List<User>().AsReadOnly());
        var actual = Assert.IsType<MissAlignmentJob>(restored);
        Assert.Equal(train, actual.DoTraining);
        Assert.Equal(general, actual.UseGeneralModel);
        Assert.Equal(train, Assert.IsType<ReadOnlyAlignMiss>(actual.AsReadOnly()).DoTraining);
    }

    [Fact]
    public void LegacySavedJobsRetainTrainingThroughNormalJobLoading()
    {
        var job = NewJob();
        var saved = Job.ToPolymorphicJson(job);
        saved["Job"]!.AsObject().Remove(nameof(job.DoTraining));
        saved["Job"]!.AsObject().Remove(nameof(job.UseGeneralModel));
        var restored = Assert.IsType<MissAlignmentJob>(Job.CreateFromPolymorphicJson(saved, job.Space, new List<User>().AsReadOnly()));
        Assert.True(restored.DoTraining);
        Assert.Equal("miss-alignment train", restored.CommandName);
        var direct = NewJob(2);
        direct.ReadFromJson(saved["Job"]!);
        Assert.True(direct.DoTraining);
    }

    [Fact]
    public void GeneralInferenceOmitsModelAndScheduleKeysAndIgnoresConnectedModel()
    {
        var job = NewJob();
        job.NCoarse = job.NFine = job.NLocal = 99;
        job.BatchSize = job.WindowSize = job.NSteps = 0;
        job.UseCtf = true;
        Connect(job.PortsIn[MissAlignmentJob.PortInModel], new MissAlignmentModel { ModelPath = "/unused/missing.ckpt" });
        job.Stage();
        var config = ReadConfig(File.ReadAllText(job.ResConfigPath));
        Assert.Single(config);
        Assert.Equal(job.DirectoryPath, config["general"]["data_directory"]);
        Assert.Equal(true, config["general"]["apply_ctf"]);
        Assert.DoesNotContain("iteration_settings", config["general"].Keys);
        Assert.DoesNotContain("model_run_directory", config["general"].Keys);
        Assert.DoesNotContain("training_directory", config["general"].Keys);
        Assert.DoesNotContain("model_training", config.Keys);
        Assert.Null(job.PortsOut[MissAlignmentJob.PortOutModel].GetResource());
    }

    [Fact]
    public void ConnectedModelSelectionRequiresTheModelInput()
    {
        var job = NewJob();
        job.UseGeneralModel = false;
        Assert.True(job.PortsIn[MissAlignmentJob.PortInModel].IsActive());
        Assert.Contains(MissAlignmentJob.PortInModel, job.ValidatePortInputs().Keys);
        Assert.Throws<InvalidOperationException>(() => job.Stage());
        Assert.False(Directory.Exists(job.DirectoryPath));
        job.DoTraining = true; // Warm-start input remains optional when training.
        Assert.Empty(job.ValidatePortInputs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectedInferenceReusesOriginalScheduleAndPatchSettings(bool legacyResource)
    {
        var trained = NewJob();
        trained.DoTraining = true;
        trained.NCoarse = trained.NFine = trained.NLocal = 1;
        trained.LocalModel = new int2(4, 5);
        trained.WindowSize = 80;
        trained.Stage();
        var model = Assert.IsType<MissAlignmentModel>(trained.PortsOut[MissAlignmentJob.PortOutModel].GetResource());
        Assert.Equal(trained.DirectoryPath, model.ModelRunDirectory);
        Assert.Equal(trained.ResConfigPath, model.ConfigPath);
        for (int i = 1; i <= 3; i++)
        {
            string directory = Path.Combine(trained.DirectoryPath, $"iter{i}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "model.ckpt"), "checkpoint");
        }
        var job = NewJob(2);
        job.UseGeneralModel = false;
        job.NCoarse = job.NFine = job.NLocal = 99;
        job.WindowSize = 0;
        if (legacyResource)
            Connect(job.PortsIn[MissAlignmentJob.PortInModel], new MissAlignmentModel { ModelPath = model.ModelPath });
        else
            job.PortsIn[MissAlignmentJob.PortInModel].Edges.Add(new Edge
            { Target = job.PortsIn[MissAlignmentJob.PortInModel], Source = trained.PortsOut[MissAlignmentJob.PortOutModel] });
        job.Stage();
        var config = ReadConfig(File.ReadAllText(job.ResConfigPath));
        Assert.Equal(job.DirectoryPath, config["general"]["data_directory"]);
        Assert.Equal(trained.DirectoryPath, config["general"]["model_run_directory"]);
        var iterations = Assert.IsAssignableFrom<IEnumerable<object>>(config["general"]["iteration_settings"]).ToArray();
        Assert.Equal(3, iterations.Length);
        Assert.Equal("anchoring", Assert.IsAssignableFrom<IDictionary>(iterations[0])["alignment"]);
        Assert.Equal(2, Convert.ToInt32(Assert.IsAssignableFrom<IDictionary>(iterations[0])["downsample"]));
        Assert.Equal("global", Assert.IsAssignableFrom<IDictionary>(iterations[1])["alignment"]);
        var local = Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsAssignableFrom<IDictionary>(iterations[2])["alignment"]);
        Assert.Equal(new[] { 4, 5 }, local.Select(Convert.ToInt32));
        Assert.Equal(80, Convert.ToInt32(config["tilt_series_alignment"]["patch_size"]));
        Assert.DoesNotContain("model_training", config.Keys);
        var passedThrough = Assert.IsType<MissAlignmentModel>(job.PortsOut[MissAlignmentJob.PortOutModel].GetResource());
        Assert.Equal(model.ModelPath, passedThrough.ModelPath);
    }

    [Fact]
    public void IncompleteConnectedRunFailsBeforeStaging()
    {
        var job = NewJob();
        job.UseGeneralModel = false;
        string models = Path.Combine(_root, "missing models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "config.yaml"),
            "general:\n  iteration_settings:\n    - {downsample: 1, alignment: global}\ntilt_series_alignment:\n  patch_size: 96\n  patch_overlap: 0.1\n  batch_size: 32\n");
        Connect(job.PortsIn[MissAlignmentJob.PortInModel], new MissAlignmentModel { ModelRunDirectory = models });
        var error = Assert.Throws<FileNotFoundException>(() => job.Stage());
        Assert.EndsWith(Path.Combine("iter1", "model.ckpt"), error.FileName);
        Assert.False(Directory.Exists(job.DirectoryPath));
    }

    [Fact]
    public void InferenceResourceRequestsIgnoreHiddenTrainingWorkerCounts()
    {
        var job = NewJob();
        int cores = job.CoreCount, memory = job.MemoryGb;
        job.NWorkers = job.PerDevice = 1000;
        Assert.Equal(cores, job.CoreCount);
        Assert.Equal(memory, job.MemoryGb);
        Assert.Equal(job.NGpus, job.GpuCount);
        job.DoTraining = true;
        Assert.True(job.CoreCount > cores);
        Assert.True(job.MemoryGb > memory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
