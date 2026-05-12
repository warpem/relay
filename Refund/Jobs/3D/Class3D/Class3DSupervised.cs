using System.Globalization;
using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Tools;

namespace Refund.Jobs._3D.Class3D;

/// <summary>
/// Represents a 3D classification job that classifies particle images into multiple 3D classes.
/// This job uses the RELION engine to perform maximum likelihood-based 3D classification of
/// particle images. It can perform classification with or without alignment, and supports
/// various refinement parameters to optimize the classification process. Unlike the unsupervised
/// version that initializes all classes with the same reference, this supervised variant takes
/// multiple initial reference maps to guide the classification into predefined structural states.
/// </summary>
/// <remarks>
/// The Class3D job is a core component of the heterogeneity analysis workflow in cryo-EM.
/// It takes a set of particle images and sorts them into distinct 3D structural classes,
/// revealing structural variability in the sample. This job type is used extensively in
/// testing and development environments, where it is often instantiated programmatically
/// and integrated with other job types like Class3DSelect for downstream processing.
/// 
/// In the application architecture, this job type is deeply integrated with the QueueRepository
/// for progress tracking and visualization generation, as well as with the DataManager for
/// job creation and cloning operations.
/// </remarks>
[GenerateReadOnly]
public class Class3DSupervised : Class3D
{
    public override string TypeGuid => "6dabfb8e-b204-4d23-b8b2-a5574d5583f8";

    /// <summary>
    /// The unique category identifier for 3D classification jobs in the job type system.
    /// </summary>
    /// <remarks>
    /// This property is used by the DataRepository during job cloning and by the DataManager
    /// during job creation through Class3DExpandedView. It uniquely identifies this job
    /// type in the system's job type registry.
    /// </remarks>
    public override string TypeCategory => "3D.Class3DSupervised";

    /// <summary>
    /// The full display name for this job type to be shown in the UI.
    /// </summary>
    /// <remarks>
    /// Used in job listings, menus, and when displaying QualifiedName properties in the
    /// user interface. Also used during job type registration in the DataModel.
    /// </remarks>
    public override string TypeName => "Supervised 3D classification";

    /// <summary>
    /// A shortened display name for this job type, used in space-constrained UI elements.
    /// </summary>
    /// <remarks>
    /// Accessed through the ReadOnlyJob wrapper for display in compact UI components.
    /// </remarks>
    public override string TypeNameShort => "Class3D super";

    /// <summary>
    /// Descriptive text explaining the purpose of this job type.
    /// </summary>
    /// <remarks>
    /// Used in job creation dialogs and tooltips to inform users about this job's functionality.
    /// Accessed through the ReadOnlyJob wrapper for display in UI elements.
    /// </remarks>
    public override string TypeDescription => "Supervised classification of particles into multiple 3D classes with or without alignment";
    
    #region Parameters
    
    [UiFieldGroup("Optimization", 1)]
    [UiStatic("K", "Number of classes",
              helpText: "The number of classes (K) for a multi-reference refinement. Set automatically based on the number of input maps.")]
    public override int NClasses
    {
        get
        {
            int nClasses = 0;
            foreach (var edge in PortsIn[PortInMaps].Edges)
                if (edge.Source.GetResource() is MapList mapList)
                    nClasses += mapList.Maps.Count;
            
            return nClasses;
        }
        set
        {}
    }

    #endregion
    
    #region Results paths

    private string ResReferenceModelStarFile => Path.Combine(DirectoryPath, "model.star");

    #endregion

    public Class3DSupervised() : base()
    {
        PortsIn[PortInMaps].MaxItems = int.MaxValue;
    }

    /// <summary>
    /// The only change needed vs. Class3D arguments is to specify the STAR file pointing to multiple references.
    /// </summary>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        // Start with base arguments from Class3D
        var result = base.ComposeCommandArguments();

        result["ref"] = Space.GetRelativePath(ResReferenceModelStarFile);

        bool needsFirstIterCc = false;
        int nClasses = 0;
        foreach (var edge in PortsIn[PortInMaps].Edges)
            if (edge.Source.GetResource() is MapList mapList)
            {
                foreach (var map in mapList.Maps)
                    if (!map.IsAbsoluteScale)
                        needsFirstIterCc = true;
                nClasses += mapList.Maps.Count;
            }
        
        result["K"] = nClasses.ToString(CultureInfo.InvariantCulture);
        
        if (needsFirstIterCc)
            result.TryAdd("firstiter_cc", "");

        return result;
    }

    public override void Stage()
    {
        base.Stage();
        
        Star table = new Star(["rlnReferenceImage"]);
        
        foreach (var edge in PortsIn[PortInMaps].Edges)
            if (edge.Source.GetResource() is MapList mapList)
                foreach (var map in mapList.Maps)
                    table.AddRow([Space.GetRelativePath(map.GetAverageOrSimilar())]);

        Star.SaveMultitable(ResReferenceModelStarFile, 
                            new() 
                            {
                                {
                                    "model_classes", table
                                } 
                            });
    }
}