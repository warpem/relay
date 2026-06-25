using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.UIFields;
using Refund.Utils;

namespace Refund.Jobs;

/// <summary>
/// Base class for all jobs that utilize the RELION software for cryo-EM data processing.
/// RELION (REgularised LIkelihood OptimisatioN) is a widely used software package for
/// high-resolution refinement of single-particle electron cryo-microscopy data.
/// </summary>
/// <remarks>
/// This class extends the base Job class with RELION-specific configuration:
/// - Adds "relion" to the required modules list
/// - Adds both "relion" and "mpi" to the supported modules list
/// - Adds a touch command suffix to create a success marker file that RELION expects
/// </remarks>
[GenerateReadOnly]
public abstract class RelionJob : Job
{
    /// <summary>
    /// Gets the modules that this job can utilize if available.
    /// Includes the base modules plus "relion" for processing and "mpi" for parallelization.
    /// </summary>
    public override string[] SupportedModules => base.RequiredModules.Concat(["relion", "mpi"]).ToArray();

    /// <summary>
    /// Gets the modules that must be available for this job to run.
    /// Includes the base required modules plus "relion".
    /// </summary>
    public override string[] RequiredModules => base.RequiredModules.Concat(["relion"]).ToArray();

    /// <summary>
    /// Gets a command suffix that creates a success marker file when the job completes successfully.
    /// RELION expects this file to exist to indicate normal termination of the job.
    /// </summary>
    public override string CommandSuffix => $" && touch {PathSuccess}";
}
