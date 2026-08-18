using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp.Tools;

namespace Refund.Jobs.Ts.Import.ImportMetadataTs;

/// <summary>
/// Job that replaces the XML metadata of a tilt series set with files from an external folder.
/// Tilt series without a counterpart in that folder keep their existing metadata.
/// </summary>
[GenerateReadOnly]
public class ImportMetadataTs : LocalJob, ILocalJob
{
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "52b16b96-085c-4114-bd3f-685bc2fca448";

    public override string TypeCategory => "Tilt-series.Import.Warp metadata";

    public override string TypeName => "Import Warp metadata";

    public override string TypeNameShort => "Import metadata";

    public override string TypeDescription => "Replaces the tilt series metadata with Warp XML files from an external folder";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// This job only copies files around, so it runs locally.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>Runs locally on the CPU; requests no GPUs.</summary>
    public override int GpuCount => 0;

    public override bool IsIterative => false;

    public override Type ExpandedViewType => null;

    public override Type CardViewType => null;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTs = "TiltSeries";
    public const string PortOutTs = "TiltSeries";

    #region Parameters

    /// <summary>
    /// Folder holding the Warp XML metadata files that will replace the existing ones.
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Metadata", 0)]
    [UiPath("metadata", "Metadata directory", SelectionMode.SingleFolder,
            helpText: "Path to a folder containing Warp XML metadata files, one per tilt series. " +
                      "Files are matched to the input tilt series by name; tilt series without a " +
                      "match keep their current metadata.")]
    public string MetadataDir { get; set; }

    #endregion

    public ImportMetadataTs()
    {
        var portInTs = new PortIn(this, typeof(TiltSeriesSet), PortInTs, "Tilt-series", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTs] = portInTs
        });

        var portOutTs = new PortOut(this, typeof(TiltSeriesSet), PortOutTs, "Tilt-series", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTs] = portOutTs
        });
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(MetadataDir))
            errors[nameof(MetadataDir)] = "Metadata directory must be specified.";
        else if (!Directory.Exists(MetadataDir))
            errors[nameof(MetadataDir)] = $"Metadata directory not found: {MetadataDir}";

        return errors;
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInTs].IsConnected)
            return null;

        var result = PortsIn[PortInTs].GetSingleResource<TiltSeriesSet>();

        if (result == null)
            throw new InvalidOperationException("Tilt-series input not found.");

        result.HasMetadata = true;
        result.LatestMetadataDirectory = DirectoryPath;

        return result;
    }

    /// <summary>
    /// Copies the input metadata into this job's directory, substituting every XML file
    /// that has a same-named counterpart in <see cref="MetadataDir"/>.
    /// </summary>
    public void RunLocal(CancellationToken token)
    {
        var tiltSeriesSet = PortsIn[PortInTs].GetSingleResource<TiltSeriesSet>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");

        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            ((StreamWriter)logger).AutoFlush = true;

            var existingNames = ListMetadataNames(tiltSeriesSet.LatestMetadataDirectory);
            logger.WriteLine($"Found {existingNames.Count} metadata files in {tiltSeriesSet.LatestMetadataDirectory}");

            var replacementNames = ListMetadataNames(MetadataDir);
            logger.WriteLine($"Found {replacementNames.Count} metadata files in {MetadataDir}");
            logger.WriteLine("");

            var replaced = new List<string>();
            var unchanged = new List<string>();

            foreach (var name in existingNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                var source = replacementNames.Contains(name)
                                 ? Path.Combine(MetadataDir, $"{name}.xml")
                                 : Path.Combine(tiltSeriesSet.LatestMetadataDirectory, $"{name}.xml");

                File.Copy(source, Path.Combine(DirectoryPath, $"{name}.xml"), true);

                (replacementNames.Contains(name) ? replaced : unchanged).Add(name);

                if (token.IsCancellationRequested)
                {
                    logger.WriteLine("Operation cancelled by user");
                    return;
                }
            }

            logger.WriteLine($"Replaced metadata for {replaced.Count} of {existingNames.Count} tilt series");

            if (unchanged.Count > 0)
            {
                logger.WriteLine("");
                logger.WriteLine($"{unchanged.Count} tilt series had no counterpart in {MetadataDir} " +
                                 "and kept their existing metadata:");

                foreach (var name in unchanged)
                    logger.WriteLine($"  {name}");
            }

            var ignored = replacementNames.Except(existingNames).OrderBy(n => n, StringComparer.Ordinal).ToList();

            if (ignored.Count > 0)
            {
                logger.WriteLine("");
                logger.WriteLine($"{ignored.Count} metadata files in {MetadataDir} match no tilt series " +
                                 "in the input set and were ignored:");

                foreach (var name in ignored)
                    logger.WriteLine($"  {name}");
            }

            if (replaced.Count == 0)
                throw new InvalidOperationException(
                    $"None of the {replacementNames.Count} metadata files in {MetadataDir} match any of the " +
                    $"{existingNames.Count} tilt series in the input set – is this the right folder?");

            logger.WriteLine("");
            logger.WriteLine("Done");
        }
    }

    /// <summary>
    /// Returns the names (without extension) of all XML metadata files in a directory,
    /// skipping hidden files such as the dot-underscore ones macOS leaves behind.
    /// </summary>
    private static HashSet<string> ListMetadataNames(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.xml")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(name => !string.IsNullOrEmpty(name) && name[0] != '.')
                        .ToHashSet(StringComparer.Ordinal);
    }
}