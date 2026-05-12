using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Jobs.Import.ImportMask;

[GenerateReadOnly]
public class ImportMask : Job, ILocalJob
{
    public override int2 CardSquareCount { set; get; } = new int2(3, 1);

    public override string TypeGuid => "c860dddc-1af1-4227-88df-28653793910f";

    public override string TypeCategory => "Import.Mask";

    public override string TypeName => "Import mask";

    public override string TypeNameShort => "Import mask";

    public override string TypeDescription => "Imports a single mask";

    public override JobQueueType QueueType => JobQueueType.Local;

    public override bool IsIterative => false;

    public override Type ExpandedViewType => typeof(ImportMaskExpandedView);


    #region Parameters

    [UiFieldGroup("Parameters", 0)]
    [UiPath("", "File path",
            SelectionMode.SingleFile,
            ["*.map" , "*.mrc"],
            helpText: "Path to the MRC file to be imported.")]
    [RelayProperty]
    public string FilePath { get; set; } = "";

    [UiDecimalNullable("", "Pixel size",
                       min: 0.001,
                       max: 1000.0,
                       stepSize: 0.001,
                       helpText: "Override the pixel size value stored in the map's header.",
                       Unit = "Å")]
    [RelayProperty]
    public decimal? PixelSize { get; set; } = null;

    #endregion

    public string ResMaskPath => Path.Combine(DirectoryPath, "mask.mrc");

    public string VisLargePath => Path.Combine(DirectoryPath, "orthoslices.png");

    public ImportMask()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var PortOutMap = new PortOut(this, typeof(Mask), "Mask", "Mask", GetMask);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutMap.Name] = PortOutMap
        });
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();
        // var propertyName = nameof(ImportMap.ImportMap.FilePath);
        // var property = typeof(ImportMap.ImportMap).GetProperty(propertyName);
        // var uiPath = property?.GetCustomAttributes(typeof(UiPath), false).FirstOrDefault() as UiPath;
        // errors[propertyName] = Helper.ValidatePath(FilePath, uiPath?.FileExtensions);

        //TODO: Implement validation for the rest of the parameters
        return errors;
    }

    private Mask GetMask(int iter)
    {
        return new Mask(ResMaskPath);
    }

    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            (logger as StreamWriter).AutoFlush = true;
            
            try
            {
                logger.WriteLine($"Importing mask from {FilePath}");

                logger.Write("Copying mask file... ");
                {
                    File.Copy(FilePath, Path.Combine(DirectoryPath, ResMaskPath));
                }
                logger.WriteLine("Done.");

                MapHeader Header = MapHeader.ReadFromFile(Path.Combine(DirectoryPath, ResMaskPath));
                logger.WriteLine($"Mask dimensions: {Header.Dimensions}");
                logger.WriteLine($"Pixel size: {Header.PixelSize.X} Å");

                logger.WriteLine("Mask imported successfully");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc.Message}");
                throw;
            }
        }
    }

    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () =>
            {
                LogsAvailableIteration = 0;
            };
        
        return null;
    }

    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration < 0 &&
            File.Exists(ResMaskPath) &&
            !File.Exists(VisLargePath))
        {
            BakeryWrapper.MapOrthosliceAtlas(ResMaskPath, 1, VisLargePath);
            BakeryWrapper.MapOrthosliceAtlas(ResMaskPath, 1, VisCard(0));

            return () =>
            {
                VisAvailableIteration = 0;
            };
        }

        return null;
    }
}