using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.WarpLive;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Jobs.Fs.WarpLive;

/// <summary>A durable preprocessing session. Each Start owns one normal local execution attempt.</summary>
[GenerateReadOnly]
public class WarpLiveJob : WarpJobGpu, ILocalJob
{
    public const string GuidString = "3ed171b9-d182-47d8-a381-e922a5ec4bf3";
    public override string TypeGuid => GuidString;
    public override string TypeCategory => "Frame-series.Motion & CTF.Warp Live";
    public override string TypeName => "Warp Live";
    public override string TypeNameShort => "Warp Live";
    public override string TypeDescription => "Live motion correction, CTF estimation, denoising and particle picking";
    public override int2 CardSquareCount { get; set; } = new(2, 1);
    public override JobQueueType QueueType => JobQueueType.Local;
    public override Type ExpandedViewType => typeof(WarpLiveExpandedView);
    public override bool IsResumable => true;
    public override bool ControlsOwnExecution => true;
    public override int GpuCount => 0;
    public override int LocalQueueSlotCount => 0;
    // The manager runs inside Relay; its existing lifecycle log is the job-card log.
    public override string NameStdOut => Path.GetRelativePath(DirectoryPath, LifecycleFilePath);
    public override Action TrackProgressLogs() => null; // Counts come from the shared session, not WarpTools stdout.

    [RelayProperty]
    public WarpLiveSettings Settings { get; set; } = new();

    public WarpLiveSessionIdentity Identity => new(Space.Project.Id, Space.Id, Id,
        DirectoryPath, Path.Combine(DirectoryPath, "tasks"));

    public WarpLiveJob()
    {
        UseWorkerPool = true;
        PortsIn = new(new Dictionary<string, PortIn>());
        PortsOut = new(new Dictionary<string, PortOut>());
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        if (!UseWorkerPool || PoolQueueId <= 0) errors[nameof(PoolQueueId)] = "Select a worker cluster queue.";
        if (NGpus < 1) errors[nameof(NGpus)] = "At least one GPU worker is required.";
        if (PerDevice < 1) errors[nameof(PerDevice)] = "At least one process per GPU is required.";
        if (string.IsNullOrWhiteSpace(Settings.InputPath)) errors[nameof(Settings)] = "Select an input directory.";
        return errors;
    }

    public override void Stage()
    {
        // The coordinator has drained the prior attempt before staging this one. Pending work
        // belongs to that attempt, while metadata, checkpoints and exports belong to the session.
        string tasks = Identity.QueueDirectory;
        if (Directory.Exists(tasks)) Directory.Delete(tasks, recursive: true);
        Directory.CreateDirectory(tasks);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "logs"));
    }

    public void RunLocal(CancellationToken token) => throw new InvalidOperationException(
        "Warp Live must run through DataManager's shared session manager.");
}
