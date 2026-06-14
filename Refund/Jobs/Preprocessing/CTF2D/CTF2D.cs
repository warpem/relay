using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Warp.Tools;

namespace Refund.Jobs.Preprocessing.CTF2D;

/// <summary>
/// Provides Contrast Transfer Function (CTF) estimation functionality for cryo-EM micrographs.
/// 
/// This job analyzes collected micrographs to determine key microscope optical parameters such as defocus, 
/// astigmatism, and resolution limits. It's used when the motion correction step is performed separately,
/// serving as a more lightweight alternative to the combined MotionAndCTF2D job.
/// </summary>
[GenerateReadOnly]
public class CTF2D : WarpJobGpu, IClusterJob
{
    /// <summary>
    /// Defines the square size for the job card display in the workflow view.
    /// Used by ReadOnlyJob to determine the visual presentation of this job in the UI.
    /// </summary>
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);

    public override string TypeGuid => "d3965d7c-f2a6-4ce4-baf4-32050104d15d";

    /// <summary>
    /// The job category used for job creation, type mapping, and organization.
    /// Referenced by DataRepository.CloneJob and other creation workflows to identify the job type.
    /// </summary>
    public override string TypeCategory => "Frame-series.Motion & CTF.CTF";
    
    /// <summary>
    /// The human-readable name displayed in the UI for this job type.
    /// Used to construct the QualifiedName and in job factory registration.
    /// </summary>
    public override string TypeName => "CTF estimation";
    
    /// <summary>
    /// A shorter name used in space-constrained UI elements.
    /// </summary>
    public override string TypeNameShort => "CTF2D";
    
    /// <summary>
    /// Description of the job's purpose, displayed in tooltips and help documentation.
    /// </summary>
    public override string TypeDescription => "CTF estimation on 2D images";
    
    /// <summary>
    /// Specifies this job requires GPU resources for optimal performance.
    /// Used by job queues to allocate appropriate computing resources.
    /// </summary>
    /// <summary>
    /// Indicates this job completes in a single execution rather than iteratively.
    /// </summary>
    public override bool IsIterative => false;
    
    /// <summary>
    /// This job doesn't provide a custom expanded view component.
    /// The ExpandedViewType is referenced by UI services to determine which component to render.
    /// </summary>
    public override Type ExpandedViewType => null;
    
    
    #region Parameters
    
    /// <summary>
    /// Defines the size of patches used for CTF estimation.
    /// Larger values include more image area for estimation but may average out local variations.
    /// </summary>
    [UiFieldGroup("Fitting parameters", 0)]
    [UiDecimal("window", "Patch size",
        helpText: "Patch size for CTF estimation in binned pixels",
        min: 256,
        max: 1536,
        stepSize: 256)]
    public decimal CTFWindow { get; set; } = 512;

    /// <summary>
    /// Controls whether to use the movie average for CTF estimation.
    /// Using the average can improve signal in challenging conditions like beam-sensitive 
    /// samples or when working without an energy filter.
    /// </summary>
    [UiBool("use_sum", "Use movie average",
        "Use the movie average spectrum instead of the average of individual frames' spectra. Can help in the absence of an energy filter, or when signal is low.")]
    public bool CTFMovieSumEnable { get; set; }

    /// <summary>
    /// Specifies the resolution of the defocus model grid in spatial and temporal dimensions.
    /// Higher grid dimensions provide more localized CTF estimation but require more 
    /// signal and computational resources.
    /// </summary>
    [UiInt3("grid", "Grid dimensions",
        helpText: "Resolution of the defocus model grid in X, Y, and temporal dimensions, separated by 'x': e.g. 5x5x40; empty = auto; Z > 1 is purely experimental")]
    public int3 CTFGridDims { get; set; } = new int3(1);

    /// <summary>
    /// The minimum resolution (maximum spacing) to consider during CTF fitting.
    /// Typically set to exclude low-resolution information that may be affected by amplitude contrast variations.
    /// </summary>
    [UiDecimal("range_min", "Minimum resolution",
        helpText: "Minimum resolution in Angstrom to consider in CTF fit",
        min: 1,
        max: 1000,
        stepSize: 1,
        unit: "Å")]
    public decimal CTFRangeMin { get; set; } = 30;

    /// <summary>
    /// The maximum resolution (minimum spacing) to consider during CTF fitting.
    /// Limited by the information content in the micrographs and the Nyquist limit.
    /// </summary>
    [UiDecimal("range_max", "Maximum resolution",
        helpText: "Maximum resolution in Angstrom to consider in CTF fit",
        min: 1,
        max: 1000,
        stepSize: 1,
        unit: "Å")]
    public decimal? CTFRangeMax { get; set; } = 4.0M;

    /// <summary>
    /// The minimum defocus value to explore during fitting.
    /// Sets the lower bound of the defocus search range, adjusted based on expected sample thickness.
    /// </summary>
    [UiDecimal("defocus_min", "Minimum defocus",
        helpText: "Minimum defocus value to explore during fitting",
        min: -1000,
        max: 1000,
        stepSize: 0.1,
        unit: "µm")]
    public decimal CTFZMin { get; set; } = 0.5M;

    /// <summary>
    /// The maximum defocus value to explore during fitting.
    /// Sets the upper bound of the defocus search range, typically adjusted based on data collection strategy.
    /// </summary>
    [UiDecimal("defocus_max", "Maximum defocus",
        helpText: "Maximum defocus value to explore during fitting",
        min: -1000,
        max: 1000,
        stepSize: 0.1,
        unit: "µm")]
    public decimal CTFZMax { get; set; } = 5.0M;

    /// <summary>
    /// The electron microscope's acceleration voltage.
    /// Affects electron wavelength calculations which are critical for accurate CTF modeling.
    /// </summary>
    [UiDecimal("voltage", "Acceleration voltage",
        helpText: "Acceleration voltage of the microscope",
        min: 10,
        max: 10000,
        stepSize: 10,
        unit: "kV")]
    public decimal CTFVoltage { get; set; } = 300;

    /// <summary>
    /// The spherical aberration coefficient of the microscope.
    /// A key optical parameter affecting the CTF, especially at higher resolutions.
    /// </summary>
    [UiDecimal("cs", "Spherical aberration",
        helpText: "Spherical aberration of the microscope",
        min: 0.01,
        max: 1000,
        stepSize: 0.01,
        unit: "mm")]
    public decimal CTFCs { get; set; } = 2.7M;

    /// <summary>
    /// The amplitude contrast of the sample, representing the fraction of electrons scattered inelastically.
    /// Typically 0.07-0.10 for cryo-EM samples; higher for negative stain.
    /// </summary>
    [UiDecimal("amplitude", "Amplitude contrast",
        helpText: "Amplitude contrast of the sample, usually 0.07-0.10 for cryo",
        min: 0.0,
        max: 1.0,
        stepSize: 0.01)]
    public decimal CTFAmplitude { get; set; } = 0.07M;

    /// <summary>
    /// Controls whether to fit for phase shift when a phase plate is used.
    /// Phase plates enhance contrast but require additional fitting parameters.
    /// </summary>
    [UiBool("fit_phase", "Fit phase shift",
        "Fit the phase shift of a phase plate")]
    public bool CTFPhaseEnable { get; set; }
    
    #endregion

    /// <summary>
    /// Initializes a new CTF2D job with the default input and output port configuration.
    /// 
    /// Sets up the job to consume DataSetFs resources which contain micrograph data for CTF estimation.
    /// </summary>
    public CTF2D()
    {
        var portInDataSet = new PortIn(
            job: this,
            resourceType: typeof(DataSetFs),
            name: "Dataset",
            alias: "Dataset",
            minItems: 1,
            maxItems: int.MaxValue
        );
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInDataSet.Name] = portInDataSet
        });
        PortsOut = new(new Dictionary<string, PortOut>());
    }

    /// <summary>
    /// Provides a mechanism for tracking progress through log file monitoring.
    /// 
    /// Called by QueueRepository during job execution to monitor processing progress.
    /// Returns null as this job doesn't implement custom log tracking.
    /// </summary>
    /// <returns>Null, as this job doesn't implement custom log tracking</returns>
    public override Action TrackProgressLogs() => null;

    /// <summary>
    /// Composes the command line for execution on a cluster.
    /// 
    /// Not implemented in this class as real execution is performed in a separate package.
    /// </summary>
    /// <returns>Throws NotImplementedException as this method is just a placeholder</returns>
    public string ComposeCommand() => throw new NotImplementedException();
}