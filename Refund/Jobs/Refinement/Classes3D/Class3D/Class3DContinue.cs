using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Classes3D.Class3D;

/// <summary>
/// Continues a previously run 3D classification job using the RELION --continue flag.
/// Locked parameters (Reference, CTF, some Optimization, and Helical settings) are read from
/// the optimizer file and hidden from the UI. Changeable parameters (TauFudge, NIterations,
/// MaskDiameter, Alignment, Compute) remain editable.
/// </summary>
[GenerateReadOnly]
public class Class3DContinue : Class3D
{
    public override string TypeGuid => "b47118e4-06c7-470f-a9f4-577048d0232c";

    public override string TypeCategory => "Refinement.3D classes.Continue 3D";

    public override string TypeName => "Continue 3D classification";

    public override string TypeNameShort => "Class3D cont";

    public override string TypeDescription => "Continue a previously paused or finished 3D classification job";

    #region Hidden parameters (locked by RELION --continue, read from optimizer file)

    // Reference group
    public override string Symmetry { get; set; } = "C1";
    public override decimal InitialLowPass { get; set; } = 60m;
    public override bool AutoResizeReference { get; set; } = true;
    public override decimal HalfmapJoinResolution { get; set; } = 40m;

    // CTF group
    public override bool DoCtfCorrection { get; set; } = true;
    public override bool IgnoreCtfUntilFirstPeak { get; set; } = false;

    // Optimization group (locked subset)
    public override int NClasses
    {
        get
        {
            if (PortsIn[PortInOptimizer].Edges.Count == 0)
                return 1; // Default to 1 class if no continued job is connected yet
            else
                return (PortsIn[PortInOptimizer].Edges.First().Source.Job as Class3D)?.NClasses ?? 1;
        }
        set { }
    }
    public override bool UseFastSubsets { get; set; } = false;
    public override bool MaskWithZeros { get; set; } = true;
    public override int LimitAlignmentResolution { get; set; } = 0;
    public override bool UseBlush { get; set; } = false;

    // Helical group (all locked)
    public override bool DoHelical { get; set; } = false;
    public override float2 HelicalTubeDiameter { get; set; } = new(-1, -1);
    public override float3 HelicalAngleRange { get; set; } = new(-1, 15, 10);
    public override decimal HelicalRangeFactor { get; set; } = -1;
    public override bool HelicalKeepTiltPriorFixed { get; set; } = true;
    public override bool HelicalApplySymmetry { get; set; } = true;
    public override int HelicalNumberUniqueUnits { get; set; } = 1;
    public override decimal HelicalTwist { get; set; } = 0m;
    public override decimal HelicalRise { get; set; } = 0m;
    public override int HelicalCentralZLength { get; set; } = 30;
    public override bool HelicalDoSymmetrySearch { get; set; } = false;
    public override float3 HelicalTwistRange { get; set; } = new(0);
    public override float3 HelicalRiseRange { get; set; } = new(0);

    #endregion

    public const string PortInOptimizer = "Optimizer";

    public Class3DContinue() : base()
    {
        // Initialize input ports
        var portInOptimizer = new PortIn(this, typeof(ContinuableClass3D), PortInOptimizer, "Continued 3D classification", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInOptimizer.Name] = portInOptimizer
        });
    }

    /// <summary>
    /// Creates a ParticleSet resource representing the output particles from this job.
    /// </summary>
    /// <param name="iter">The iteration number to retrieve particles from, or -1 for the latest available iteration</param>
    /// <returns>A ParticleSet with paths to the classified particle data and relevant metadata flags</returns>
    protected override ParticleSet GetParticlesResource(int iter)
    {
        // Use the latest available iteration if not specified
        if (iter == -1)
            iter = VisAvailableIteration;

        if (PortsIn[PortInOptimizer].Edges.Count == 0)
            return null;

        // Start with input particles and update with classification results
        ParticleSet result = (PortsIn[PortInOptimizer].Edges.First().Source.Job as Class3D)?.PortsOut[PortOutParticles].GetResource() as ParticleSet;
        if (result == null)
            return null;

        // Update the path to point to the classified particles
        result.ParticlesSingleStarPath = ResDataStarFile(iter);

        // Set flags indicating that these particles have class assignments, scale factors, and orientation angles
        result.HasClasses = true;
        result.HasScale = true;
        result.HasAngles = true;

        if (result.IsTomo)
            result.OptimisationSetStarPath = ResOptimizationSetStarFile(iter);

        return result;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        // Build arguments from scratch using only the changeable parameters,
        // rather than calling base which would crash on empty Particles/Maps ports.
        var result = new Dictionary<string, string>();
        
        ContinuableClass3D continuedJob = PortsIn[PortInOptimizer].GetSingleResource<ContinuableClass3D>();
        if (continuedJob == null)
            throw new InvalidOperationException("No continuable 3D classification job provided.");
        
        // The --continue flag with the path to the previous optimizer file
        result["continue"] = Space.GetRelativePath(continuedJob.OptimizerStarPath);

        // Changeable Optimization parameters
        result["tau2_fudge"] = TauFudge.ToString(CultureInfo.InvariantCulture);
        result["iter"] = NIterations.ToString(CultureInfo.InvariantCulture);
        result["particle_diameter"] = MaskDiameter.ToString(CultureInfo.InvariantCulture);

        // Alignment parameters
        if (!DoAlignment)
            result["skip_align"] = "";
        result["healpix_order"] = Math.Max(1, HealpixOrder - 1).ToString(CultureInfo.InvariantCulture);
        result["offset_range"] = OffsetRange.ToString(CultureInfo.InvariantCulture);
        result["offset_step"] = (OffsetSampling * 2).ToString(CultureInfo.InvariantCulture);
        if (AllowCoarserSampling)
            result["allow_coarser_sampling"] = "";
        if (AlignmentType == Class3DAlignmentType.Local)
        {
            result["sigma_ang"] = (AngularSearchRange / 3).ToString("F3", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(RelaxSymmetry))
                result["relax_sym"] = RelaxSymmetry;
        }

        // Helical parameters are locked by --continue and read from the optimizer file

        // Compute parameters
        if (!string.IsNullOrWhiteSpace(UseScratch))
            result["scratch_dir"] = UseScratch;
        if (UseGpu)
            result["gpu"] = "";
        result["j"] = NThreads.ToString(CultureInfo.InvariantCulture);

        // Additional arguments
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        // Standard RELION parameters
        result.TryAdd("pool", "10");
        result.TryAdd("pad", "2");
        result.TryAdd("dont_combine_weights_via_disc", "");
        result.TryAdd("oversampling", "1");

        result.TryAdd("pipeline_control", DirectoryName);

        // Output prefix
        result["o"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "run"));

        // This subclass builds its args from scratch (base would crash on the empty Particles/Maps
        // ports), so it must apply the pool overrides itself — mirroring Class3D.ComposeCommandArguments.
        // Applied last so pool-owned args (--j, --pool_dir, --pool_batch; strip --gpu/--scratch_dir)
        // win over the compute args set above. The worker command re-adds --gpu/--gpu_shares as needed.
        if (IsPooled)
            ApplyPoolArguments(result);

        return result;
    }

    /// <summary>
    /// True if a predecessor-relative path belongs to worker-pool state that must NOT be copied into a
    /// continued job: the RELION coordination directory (--pool_dir, <see cref="PoolDirName"/>) and
    /// Relay's WorkerPool state/logs/script (pool_state.json, worker_logs, worker_submit.sh — see
    /// <c>WorkerPool</c>). Copying them would corrupt this job's fresh pool with the previous run's
    /// worker registrations, task queue, and submitted-id bookkeeping.
    /// </summary>
    public bool IsPoolArtifact(string relativePath)
    {
        var top = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return top == PoolDirName
            || top == "worker_logs"
            || top == "pool_state.json"
            || top == "worker_submit.sh";
    }

    public override void Stage()
    {
        base.Stage();

        // Copy all data and visualizations, including hidden folders, from the continued job's directory
        // to the new job's directory — except the previous run's worker-pool artifacts (see IsPoolArtifact).
        var predecessor = PortsIn[PortInOptimizer].Edges.First().Source.Job as Class3D;
        foreach (var filePath in Directory.EnumerateFiles(predecessor.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(predecessor.DirectoryPath, filePath);
            if (IsPoolArtifact(relativePath))
                continue;
            var destPath = Path.Combine(DirectoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            File.Copy(filePath, destPath, overwrite: true);
        }

        LogsAvailableIteration = predecessor.LogsAvailableIteration;
        VisAvailableIteration = predecessor.VisAvailableIteration;
        ContinuingFromIteration = predecessor.VisAvailableIteration + 1;
        
        if (File.Exists(PathStdOut))
            File.Delete(PathStdOut);
        if (File.Exists(PathStdErr))
            File.Delete(PathStdErr);
    }
}
