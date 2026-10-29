using System.Diagnostics;
using System.Reflection;
using Refund.DataModel;
using Refund.JobResources;
using Refund.JobQueues;
using Refund.Jobs.Common.Import.ImportParticles;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class ImportParticlesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay import " + Guid.NewGuid().ToString("N"));
    private ImportParticles NewJob()
    {
        JobRegistry.EnsurePopulated();
        return new ImportParticles { Id = 1, Space = new Space { RootDirectory = _root } };
    }

    [Fact]
    public void FormatSpecificPickersHaveOneExtensionAndCorrectVisibility()
    {
        var job = NewJob();
        var fields = new[] { nameof(job.CryoSparcFilePath), nameof(job.AdditionalMetadataFile1),
            nameof(job.AdditionalMetadataFile2), nameof(job.AdditionalMetadataFile3),
            nameof(job.AdditionalMetadataFile4), nameof(job.AdditionalMetadataFile5) };
        var relion = typeof(ImportParticles).GetProperty(nameof(job.FilePath))!;
        Assert.Equal(new[] { "*.star" }, relion.GetCustomAttribute<UiPath>()!.FileExtensions);
        Assert.True(Job.IsPropertyVisible(typeof(ImportParticles), relion, job.AsReadOnly()));
        foreach (string field in fields)
        {
            var property = typeof(ImportParticles).GetProperty(field)!;
            Assert.Equal(new[] { "*.cs" }, property.GetCustomAttribute<UiPath>()!.FileExtensions);
            Assert.False(Job.IsPropertyVisible(typeof(ImportParticles), property, job.AsReadOnly()));
        }
        job.MetadataFormat = ParticleMetadataFormat.CryoSparc;
        Assert.False(Job.IsPropertyVisible(typeof(ImportParticles), relion, job.AsReadOnly()));
        foreach (string field in fields)
            Assert.True(Job.IsPropertyVisible(typeof(ImportParticles), typeof(ImportParticles).GetProperty(field)!, job.AsReadOnly()));
    }

    [Fact]
    public void CryoSparcPathsAreIndependentAndOptionalFieldsAllowGaps()
    {
        var job = NewJob();
        job.MetadataFormat = ParticleMetadataFormat.CryoSparc;
        job.FilePath = "/data/original.star";
        job.CryoSparcFilePath = "/data/project/J2/particles.cs";
        job.AdditionalMetadataFile1 = "J2/pass through.cs";
        job.AdditionalMetadataFile5 = "J1/locations.cs";
        Assert.Contains(nameof(job.CryoSparcProjectDirectory), job.ValidateInputs().Keys);
        job.CryoSparcProjectDirectory = "/data/project";
        Assert.Empty(job.ValidateInputs());
        Assert.Equal(new[] { job.CryoSparcFilePath, "J2/pass through.cs", "J1/locations.cs" }, job.CryoSparcMetadataPaths);
        job.AdditionalMetadataFile3 = "bad.star";
        Assert.Contains(nameof(job.AdditionalMetadataFile3), job.ValidateInputs().Keys);
        job.MetadataFormat = ParticleMetadataFormat.Relion;
        Assert.Empty(job.ValidateInputs());
        Assert.Equal("/data/original.star", job.FilePath);
        Assert.Equal("/data/project/J2/particles.cs", job.CryoSparcFilePath);
    }

    [Fact]
    public void ImportUsesCpuQueueAndBeamModuleForBothFormats()
    {
        var job = NewJob();
        Assert.IsAssignableFrom<IClusterJob>(job);
        Assert.False(job is ILocalJob);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(new[] { "beam", "cpu" }, job.RequiredModules);
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(8, job.CoreCount);
        Assert.Equal("beam import", job.CommandName);
        job.FilePath = "/data/particles.star";
        var args = job.ComposeCommandArguments();
        Assert.Equal("/data", args["project-root"]);
        Assert.Equal(job.ImportedParticlesPath, args["output"]);
        Assert.True(args.ContainsKey("absolute-paths"));
        Assert.True(args.ContainsKey("check-stacks"));
        Assert.Equal("8", args["workers"]);
        job.Workers = 16;
        Assert.Equal(16, job.CoreCount);
        Assert.Equal("16", job.ComposeCommandArguments()["workers"]);
        job.Workers = 0;
        Assert.Contains(nameof(job.Workers), job.ValidateInputs().Keys);
        job.Workers = 8;
        job.RelionProjectDirectory = "/data/relion_project";
        Assert.Equal(job.RelionProjectDirectory, job.ComposeCommandArguments()["project-root"]);
    }

    [Fact]
    public void ClusterCommandPassesAllSixInputsAsSeparateLiteralArguments()
    {
        var job = NewJob();
        job.MetadataFormat = ParticleMetadataFormat.CryoSparc;
        job.CryoSparcProjectDirectory = "/data/project";
        job.CryoSparcFilePath = "/data/project/particles.cs";
        job.AdditionalMetadataFile1 = "pass through.cs";
        job.AdditionalMetadataFile2 = "quote'file.cs";
        job.AdditionalMetadataFile3 = "$(false).cs";
        job.AdditionalMetadataFile4 = "four.cs";
        job.AdditionalMetadataFile5 = "five.cs";
        string suffix = job.CommandSuffix[..job.CommandSuffix.LastIndexOf(" && touch", StringComparison.Ordinal)];
        var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("printf '%s\n' " + suffix);
        using var process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(new[] { "--input" }.Concat(job.CryoSparcMetadataPaths), stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task SubmissionIncludesBeamModuleAndQuotesTheWorkingDirectory()
    {
        var job = NewJob();
        job.MetadataFormat = ParticleMetadataFormat.CryoSparc;
        job.CryoSparcFilePath = "/source project/particles.cs";
        job.CryoSparcProjectDirectory = "/source project";
        job.AdditionalMetadataFile5 = "/source project/passthrough.cs";
        var queue = new ClusterQueue
        {
            SubmissionScriptTemplate = "#SBATCH --cpus-per-task {{n_cores}}\n{{cpu}}# cpu import\n{{/cpu}}{{beam}}ml beam\n{{/beam}}{{gpu}}# gpu-only\n{{/gpu}}{{command}}"
        };
        string script = await File.ReadAllTextAsync(await queue.PrepareAndWriteScript(job, Guid.NewGuid()));
        Assert.Contains("ml beam", script);
        Assert.Contains("#SBATCH --cpus-per-task 8", script);
        Assert.Contains("--workers \"8\"", script);
        Assert.DoesNotContain("gpu-only", script);
        Assert.Contains("cd \"" + _root + "\"", script);
        Assert.Contains("beam import", script);
        Assert.Contains("'/source project/particles.cs' '/source project/passthrough.cs'", script);
    }

    [Fact]
    public void CompletedOutputIsCollectedOnceWithoutInvokingBeamOnTheRelayHost()
    {
        var job = NewJob();
        Write(job.ImportedParticlesPath, "data_particles\n\nloop_\n_rlnImageName #1\n_rlnCoordinateX #2\n_rlnCoordinateY #3\n_rlnDefocusU #4\n'1@/data/stack file.mrcs' 20 30 10000\n");
        Write(job.VisParticlesPath, "preview");
        Write(job.VisCard(0), "card");
        Write(job.PathStdOut, "Imported 1 particle\n");
        Write(job.PathStdErr, "warning\n");
        job.TrackProgressLogs()!.Invoke();
        job.TrackProgressResults()!.Invoke();
        Assert.True(job.HasPositions);
        Assert.True(job.HasCtf);
        Assert.True(job.HasRunToCompletion);
        Assert.Equal(-1, job.VisAvailableIteration);
        job.TrackProgressResults()!.Invoke();
        Assert.Equal(0, job.VisAvailableIteration);
        Assert.Equal("Imported 1 particle\n", File.ReadAllText(job.LogFilePath(0)));
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
        Assert.Null(job.TrackProgressResults());
        var particles = Assert.IsType<ParticleSet>(job.PortsOut["Particles"].GetResource());
        Assert.True(particles.HasPositions);
        Assert.Equal(job.ImportedParticlesPath, particles.ParticlesSingleStarPath);
    }

    [Fact]
    public void PublishedMetadataEnablesDownstreamCtfBeforePreviewsOrSuccessMarker()
    {
        var job = NewJob();
        Assert.Null(job.TrackProgressResults());
        Write(job.ImportedParticlesPath, "data_particles\n\nloop_\n_rlnImageName #1\n_rlnDefocusU #2\n_rlnDefocusV #3\n_rlnDefocusAngle #4\n_rlnAnglePsi #5\n_rlnOriginXAngst #6\n_rlnCoordinateX #7\n1@/data/stack.mrcs 10000 11000 20 30 1 50\n");
        job.TrackProgressResults()!.Invoke();
        Assert.True(job.HasCtf);
        Assert.True(job.HasAngles);
        Assert.True(job.HasShifts);
        Assert.True(job.HasPositions);
        Assert.False(File.Exists(job.PathSuccess));
        Assert.False(File.Exists(job.VisParticlesPath));
        Assert.Equal(-1, job.VisAvailableIteration);

        var refine = new BeamRefine3D { Id = 2, Space = job.Space, CtfFit = BeamCtfFit.DefocusAndAberrations, Ewald = BeamEwald.Positive };
        refine.PortsIn["Particles"].Edges.Add(new Edge { Source = job.PortsOut["Particles"], Target = refine.PortsIn["Particles"] });
        Assert.DoesNotContain("Particles", refine.ValidatePortInputs().Keys);
    }

    [Fact]
    public void LogsAreRefreshedAfterEmptyStartupAndWithoutDependingOnFileLength()
    {
        var job = NewJob();
        Write(job.PathStdOut, "");
        Write(job.PathStdErr, "");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal("", File.ReadAllText(job.LogFilePath(0)));

        Write(job.PathStdOut, "Reading metadata\n");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal("Reading metadata\n", File.ReadAllText(job.LogFilePath(0)));

        // Same length: content collection must not depend on an observed size change.
        Write(job.PathStdOut, "Imported results\n");
        Write(job.PathStdErr, "warning\n");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal("Imported results\n", File.ReadAllText(job.LogFilePath(0)));
        Assert.Equal("warning\n", File.ReadAllText(job.ErrorFilePath));
        Assert.Equal(0, job.LogsAvailableIteration);
    }

    [Fact]
    public void CarriageReturnProgressCollapsesToItsLatestUpdate()
    {
        var job = NewJob();
        Write(job.PathStdOut, "Read 50000 particles\n\rChecking: 1/100\rChecking: 50/100\rChecking: 100/100\nImported results\n");
        job.TrackProgressLogs()!.Invoke();
        Assert.Equal("Read 50000 particles\nChecking: 100/100\nImported results\n", File.ReadAllText(job.LogFilePath(0)));
    }

    [Fact]
    public void MetadataDetectionPreservesPositionsAndResetsFlagsOnReimport()
    {
        var job = NewJob();
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

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
