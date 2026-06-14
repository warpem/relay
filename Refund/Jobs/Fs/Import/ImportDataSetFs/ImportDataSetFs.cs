using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Jobs.Fs.Import.ImportDataSetFs;

[GenerateReadOnly]
public class ImportDataSetFs : Job, ILocalJob
{
    public override string TypeGuid => "56b8f795-1010-4cbb-9b43-208e8c879f09";
    public override string TypeCategory => "Frame-series.Import.Frame series";

    public override string TypeName => "Frame-series data set";

    public override string TypeNameShort => "DataSetFs";

    public override string TypeDescription => "Specifies essential parameters for importing a set of frame-series";

    public override JobQueueType QueueType => JobQueueType.Local;

    public override bool IsIterative => false;

    public override Type ExpandedViewType => null;

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);


    #region Parameters
    
    #region Data location
    
    [UiFieldGroup("Data location", 0)]
    [UiPath("", "Data directory",
            SelectionMode.SingleFolder,
            helpText: "The root directory containing (subdirectories with) files.")]
    [RelayProperty]
    public string DataDirectory { get; set; } = "";

    [UiString("", "File search pattern",
              helpText: "The search pattern for files in the root directory (and its subdirectories).")]
    [RelayProperty]
    public string FileSearchPattern { get; set; } = "*.eer";

    [UiBool("", "Search recursively",
            helpText: "Whether to search recursively for files in subdirectories of the root directory.")]
    [RelayProperty]
    public bool DoRecursiveSearch { get; set; } = false;
    
    #endregion

    #region EER
    
    [UiFieldGroup("EER", 1)]
    [UiInt("", "Number of groups", min: 1, max: 1000, stepSize: 1,
            helpText: "Only if importing EER files: number of groups to combine raw EER frames into, i.e. number of 'virtual' frames in resulting stack.")]
    [RelayProperty]
    public int EerFrames { get; set; } = 40;
    
    #endregion

    #region Correction
    
    [UiFieldGroup("Sensor correction", 1)]
    [UiPath("", "Gain reference path",
            SelectionMode.SingleFile,
            ["*.mrc","*.gain"],
            helpText: "Path to the gain reference file used to correct the sensor gain pattern in raw data.")]
    [RelayProperty]
    public string GainPath { get; set; } = "";
    
    [UiPath("", "Defects reference path",
            SelectionMode.SingleFile,
            ["*.mrc"],
            helpText: "Path to the defects reference file used to correct bad pixels. 0 = good pixel, 1 = bad pixel")]
    [RelayProperty]
    public string DefectsPath { get; set; } = "";

    [UiBool("", "Flip X",
            helpText: "Flip references along the X axis to match images.")]
    [RelayProperty]
    public bool GainFlipX { get; set; } = false;

    [UiBool("", "Flip Y",
            helpText: "Flip references along the Y axis to match images.")]
    [RelayProperty]
    public bool GainFlipY { get; set; } = false;

    [UiBool("", "Transpose",
            helpText: "Transpose references (swap X and Y axes) to match images.")]
    [RelayProperty]
    public bool GainTranspose { get; set; } = false;
    
    #endregion

    #region Microscope parameters
    
    [UiFieldGroup("Microscope parameters", 2)]
    [UiDecimal("", "Pixel size",
               min: 0.001,
               max: 1000.0,
               stepSize: 0.001,
               helpText: "The image pixel size in Angstrom, not accounting for any super-resolution.",
               Unit = "Å")]
    [RelayProperty]
    public decimal PixelSize { get; set; } = 1.0m;
    
    [UiDecimal("", "Binning factor",
               min: 0,
               max: 1000.0,
               stepSize: 0.001,
               helpText: "The images will be binned such that the final pixel size is (pixel size * binning factor).")]
    [RelayProperty]
    public decimal BinFactor { get; set; } = 1.0m;
    
    [UiDecimal("", "Overall exposure",
               min: 0,
               max: 100000.0,
               stepSize: 0.1,
               helpText: "Overall exposure of the movie in e-/Å².",
               Unit = "e-/Å²")]
    [RelayProperty]
    public decimal OverallExposure { get; set; } = 40m;

    [UiDecimal("", "Spherical aberration",
               min: 0.0,
               max: 100.0,
               stepSize: 0.01,
               helpText: "The spherical aberration of the microscope.",
               Unit = "mm")]
    [RelayProperty]
    public decimal Cs { get; set; } = 2.7m;

    [UiDecimal("", "Acceleration voltage",
               min: 0.0,
               max: 10000.0,
               stepSize: 1,
               helpText: "The acceleration voltage of the microscope.",
               Unit = "kV")]
    [RelayProperty]
    public decimal Voltage { get; set; } = 300m;

    [UiDecimal("", "Amplitude contrast",
               min: 0.0,
               max: 1.0,
               stepSize: 0.01,
               helpText: "The expected amplitude contrast in the images.",
               Unit = "")]
    [RelayProperty]
    public decimal AmplitudeContrast { get; set; } = 0.1m;
    
    #endregion
    
    #endregion

    public ImportDataSetFs()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var portOutDataSet = new PortOut(this, typeof(DataSetFs), "DataSet", "Data set", GetDataSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutDataSet.Name] = portOutDataSet
        });
    }

    private DataSetFs GetDataSetResource(int iter)
    {
        return new DataSetFs()
        {
            DataDirectory = DataDirectory,
            FileSearchPattern = FileSearchPattern,
            DoRecursiveSearch = DoRecursiveSearch,
            
            GainPath = GainPath,
            DefectsPath = DefectsPath,
            GainFlipX = GainFlipX,
            GainFlipY = GainFlipY,
            GainTranspose = GainTranspose,
            
            EerFrames = EerFrames,
            
            PixelSize = PixelSize,
            BinFactor = BinFactor,
            OverallExposure = OverallExposure,
            Cs = Cs,
            Voltage = Voltage,
            AmplitudeContrast = AmplitudeContrast
        };
    }

    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            (logger as StreamWriter).AutoFlush = true;
            
            try
            {
                logger.WriteLine($"Looking for data in {DataDirectory}...");
                
                List<string> files = Directory.GetFiles(DataDirectory, 
                                                        FileSearchPattern, 
                                                        DoRecursiveSearch ? 
                                                            SearchOption.AllDirectories : 
                                                            SearchOption.TopDirectoryOnly).ToList();
                
                logger.WriteLine($"Found {files.Count} files.");
                
                if (!string.IsNullOrWhiteSpace(GainPath) && !File.Exists(GainPath))
                    throw new FileNotFoundException($"Gain reference file not found: {GainPath}");
                
                if (!string.IsNullOrWhiteSpace(DefectsPath) && !File.Exists(DefectsPath))
                    throw new FileNotFoundException($"Defects reference file not found: {DefectsPath}");

                if (files.Count > 0)
                {
                    int2 dimsImage = new int2(MapHeader.ReadFromFile(files.First()).Dimensions);
                    logger.WriteLine($"Image dimensions: {dimsImage}");
                    
                    if (!string.IsNullOrWhiteSpace(GainPath))
                    {
                        int2 dimsGain = new int2(MapHeader.ReadFromFile(GainPath).Dimensions);
                        if (GainTranspose)
                            dimsGain = new int2(dimsGain.Y, dimsGain.X);
                        logger.WriteLine($"Gain reference dimensions (with transform applied): {dimsGain}");
                        
                        if (dimsImage != dimsGain)
                            throw new Exception("Image and gain reference dimensions do not match");
                    }
                    
                    if (!string.IsNullOrWhiteSpace(DefectsPath))
                    {
                        int2 dimsDefects = new int2(MapHeader.ReadFromFile(DefectsPath).Dimensions);
                        if (GainTranspose)
                            dimsDefects = new int2(dimsDefects.Y, dimsDefects.X);
                        logger.WriteLine($"Defects reference dimensions (with transform applied): {dimsDefects}");
                        
                        if (dimsImage != dimsDefects)
                            throw new Exception("Image and defects reference dimensions do not match");
                    }
                }
                else
                {
                    logger.WriteLine("No images found, so won't check dimensions");
                }
                
                logger.WriteLine("Data set imported successfully");

                if (files.Count >= 2)
                {
                    string file1 = files[0];
                    string file2 = files[files.Count / 2];

                    logger.WriteLine($"Generating card from {Path.GetFileName(file1)} and {Path.GetFileName(file2)}...");
                    BakeryWrapper.ImportFsJobCard(file1, file2, VisCard(0));
                    logger.WriteLine("Card generated.");
                }
                else if (files.Count == 1)
                {
                    logger.WriteLine($"Generating card from {Path.GetFileName(files[0])}...");
                    BakeryWrapper.ImportFsJobCard(files[0], files[0], VisCard(0));
                    logger.WriteLine("Card generated.");
                }
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
            return () => LogsAvailableIteration = 0;
        
        return null;
    }

    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration < 0 && File.Exists(VisCard(0)))
            return () => VisAvailableIteration = 0;

        return null;
    }
}