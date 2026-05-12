using System.Diagnostics;
using Serilog;

namespace Refund.Utils;

/// <summary>
/// Provides a wrapper around the Bakery command-line tool for creating visualizations
/// of cryo-EM data such as maps, masks, Fourier shell correlation curves, and particle distributions.
/// </summary>
/// <remarks>
/// Bakery is a Python package for creating publication-quality visualizations for cryo-electron microscopy data.
/// This wrapper provides easy access to common Bakery visualization functions from C# code,
/// by constructing and executing the appropriate command-line arguments.
/// 
/// This class is extensively used by various job types to generate visualizations for their specific data:
/// - 3D refinement jobs use it to generate map orthoslices, FSC plots, and angular distribution visualizations
/// - Classification jobs use it for class average atlases and class-specific FSC plots
/// - Preprocessing jobs use it to visualize motion correction and particle picking results
/// - Post-processing jobs use it to generate FSC, Guinier plots and filtered map visualizations
/// 
/// Visualizations are typically generated asynchronously in task-based workflows and saved to the job's
/// visualization directory for display in job cards and expanded views.
/// </remarks>
public static class BakeryWrapper
{
    /// <summary>
    /// Maximum number of concurrent bakery processes that can run simultaneously.
    /// </summary>
    private const int MaxConcurrentProcesses = 4;

    /// <summary>
    /// Semaphore to limit the number of concurrent bakery processes.
    /// </summary>
    private static readonly SemaphoreSlim ProcessSemaphore = new(MaxConcurrentProcesses, MaxConcurrentProcesses);
    /// <summary>
    /// Executes a Bakery command with the specified arguments and working directory.
    /// Limits the number of concurrent processes to MaxConcurrentProcesses using a semaphore.
    /// </summary>
    /// <param name="command">The command-line arguments to pass to Bakery.</param>
    /// <param name="workingDirectory">The working directory for the command execution. If empty, uses the current directory.</param>
    private static void RunCommand(string command, string workingDirectory = "")
    {
        ProcessSemaphore.Wait(); // Block if maximum concurrent processes reached
        
        try
        {
            Process process = new()
            {
                StartInfo = new()
                {
                    FileName = "bakery",
                    WorkingDirectory = workingDirectory,
                    Arguments = command,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };

            DataReceivedEventHandler Handler = (sender, args) =>
            {
                if(args.Data != null) 
                    Log.ForContext("SourceContext", "Refund.Utils.BakeryWrapper").Error("Bakery process output: {Output}", args.Data);
            };

            process.ErrorDataReceived += Handler;

            try
            {
                Log.ForContext("SourceContext", "Refund.Utils.BakeryWrapper").Information("Running bakery with: {Output}", command);
                
                process.Start();
                process.BeginErrorReadLine();

                process.WaitForExit();
            }
            catch(Exception ex)
            {
                Log.ForContext("SourceContext", "Refund.Utils.BakeryWrapper").Error(ex, "Bakery command execution failed");
            }
        }
        finally
        {
            ProcessSemaphore.Release(); // Always release the semaphore, even if an exception occurs
        }
    }

    /// <summary>
    /// Creates an orthoslice atlas visualization from a 3D density map.
    /// </summary>
    /// <param name="volumeFile">Path to the input 3D volume file (typically in MRC format).</param>
    /// <param name="sliceThicknessPx">Thickness of each slice in pixels.</param>
    /// <param name="outputFile">Path where the output visualization image will be saved.</param>
    /// <remarks>
    /// This generates a visualization showing orthogonal slices through the 3D volume, 
    /// typically for density maps from cryo-EM reconstructions.
    /// 
    /// Extensively used in multiple job types:
    /// - In PostProcess3D jobs to visualize resolution filtered maps
    /// - In Class3D jobs to generate orthoslice visualizations for each class
    /// - In ImportMap jobs to create initial map visualizations
    /// - In Refine3D jobs to visualize half-maps and final refined maps
    /// 
    /// The visualization is typically generated asynchronously in VisTasks to avoid blocking the main thread.
    /// </remarks>
    public static void MapOrthosliceAtlas(
        string volumeFile,
        int sliceThicknessPx,
        string outputFile)
    {
        RunCommand(
            $"map-orthoslice-atlas --volume-file {volumeFile} " +
            $"--slice-thickness-px {sliceThicknessPx} " +
            $"--output-file {outputFile}"
        );
    }

    /// <summary>
    /// Creates an orthoslice atlas visualization from a 3D mask volume.
    /// </summary>
    /// <param name="volumeFile">Path to the input 3D mask volume file.</param>
    /// <param name="sliceThicknessPx">Thickness of each slice in pixels.</param>
    /// <param name="binarize">Whether to binarize the mask value (convert to 0 or 1 based on threshold).</param>
    /// <param name="binarizationThreshold">Threshold value for binarization (if binarize is true).</param>
    /// <param name="outputFile">Path where the output visualization image will be saved.</param>
    /// <remarks>
    /// This generates a visualization showing orthogonal slices through a 3D mask volume.
    /// Masks are typically used to define regions of interest in cryo-EM reconstructions.
    /// </remarks>
    public static void MaskOrthosliceAtlas(
        string volumeFile,
        int sliceThicknessPx,
        bool binarize,
        float binarizationThreshold,
        string outputFile)
    {
        var command = $"mask-orthoslice-atlas --volume-file {volumeFile} " +
                      $"--slice-thickness-px {sliceThicknessPx} " +
                      $"--output-file {outputFile} ";

        if(binarize == true)
        {
            command += "--binarize ";
            command += $"--binarization-threshold {binarizationThreshold} ";
        }

        RunCommand(command);
    }

    /// <summary>
    /// Creates an orthoslice atlas with isolines (contour lines) from a 3D mask.
    /// </summary>
    /// <param name="maskFile">Path to the input 3D mask file.</param>
    /// <param name="sliceThicknessPx">Thickness of each slice in pixels.</param>
    /// <param name="isolineThreshold">Threshold value for generating isolines.</param>
    /// <param name="outputFile">Path where the output visualization image will be saved.</param>
    /// <remarks>
    /// Generates atlas images containing isolines (contour lines) of central slices through a 3D mask.
    /// This helps visualize the boundaries of regions of interest in a cryo-EM reconstruction.
    /// </remarks>
    public static void MaskOrthosliceIsolineAtlas(
        string maskFile,
        int sliceThicknessPx,
        float isolineThreshold,
        string outputFile
    )
    {
        // generate atlas images containing isolines of central slices through a 3D mask
        RunCommand(
            $"mask-orthoslice-isoline-atlas --volume-file {maskFile} " +
            $"--slice-thickness-px {sliceThicknessPx} " +
            $"--isoline-threshold {isolineThreshold} " +
            $"--output-file {outputFile} "
        );
    }

    /// <summary>
    /// Generates a Fourier Shell Correlation (FSC) plot from a RELION model.star file.
    /// </summary>
    /// <param name="starFile">Path to the input RELION model.star file.</param>
    /// <param name="outputFile">Path where the output FSC plot image will be saved.</param>
    /// <remarks>
    /// FSC plots are used to assess the resolution of cryo-EM reconstructions by comparing
    /// two independent half-maps and measuring their correlation in Fourier space.
    /// 
    /// Used primarily in Refine3D jobs to visualize resolution assessment for both intermediate 
    /// iterations and the final refinement result. The visualization shows the correlation between 
    /// half-maps at different spatial frequencies, indicating the resolution achieved.
    /// 
    /// The output image is typically stored in the job's visualization directory and referenced
    /// in job cards and expanded views to provide resolution metrics to the user.
    /// </remarks>
    public static void FSCFromModelStar(
        string starFile,
        string outputFile
    )
    {
        RunCommand(
            $"fsc --input-type relion_model_star " +
            $"--metadata-file {starFile} " +
            $"--output-file {outputFile}"
        );
    }

    /// <summary>
    /// Generates FSC and Guinier plots from a RELION postprocessing star file.
    /// </summary>
    /// <param name="starFile">Path to the input RELION postprocess.star file.</param>
    /// <param name="outputFileFsc">Path where the output FSC plot image will be saved.</param>
    /// <param name="outputFileGuinier">Path where the output Guinier plot image will be saved.</param>
    /// <remarks>
    /// Generates two visualization plots from RELION postprocessing results:
    /// 1. FSC plot showing resolution assessment
    /// 2. Guinier plot showing B-factor sharpening effects
    /// </remarks>
    public static void PostProcess3DFSCAndGuinier(
        string starFile,
        string outputFileFsc,
        string outputFileGuinier
    )
    {
        RunCommand(
            $"postprocess3d-fsc-and-guinier " +
            $"--postprocess-star-file {starFile} " +
            $"--output-file-fsc {outputFileFsc} " +
            $"--output-file-guinier {outputFileGuinier} "
        );
    }

    /// <summary>
    /// Generates FSC plots for each class from a RELION Class3D job.
    /// </summary>
    /// <param name="starFile">Path to the input RELION model.star file from a Class3D job.</param>
    /// <param name="outputFile">Base path for the output FSC plot images. Class-specific suffixes will be added.</param>
    /// <remarks>
    /// Creates separate FSC plots for each 3D class from a RELION 3D classification job.
    /// Output filenames will include a class suffix (e.g., output.png -> output_class003.png).
    /// 
    /// Utilized in both Class3D and InitialReference3D jobs to generate per-class FSC plots 
    /// for quality assessment of each 3D class. These plots help users evaluate the resolution 
    /// and quality of individual classes to assist in selecting the best classes for further processing.
    /// 
    /// The plots are typically generated asynchronously in VisTasks and saved to the job's visualization
    /// directory for later display.
    /// </remarks>
    public static void Class3DPerClassFscPlots(
        string starFile,
        string outputFile
    )
    {
        /* Input file is a model.star from RELION Class3D
         * Output filenames per class will include a `_class{class_number:03d)` suffix
         *
         * e.g.
         * - output.png -> output_class003.png
         */
        RunCommand(
            $"class3d-fsc-per-class " +
            $"--input-file {starFile} " +
            $"--output-file {outputFile}"
        );
    }

    /// <summary>
    /// Generates orientation distribution and Fourier sampling completeness visualizations for particles.
    /// </summary>
    /// <param name="particlesFile">Path to the input particle STAR file.</param>
    /// <param name="outputOrientationFile">Path where the orientation distribution plot will be saved (optional).</param>
    /// <param name="outputFourierSamplingFile">Path where the Fourier sampling completeness plot will be saved (optional).</param>
    /// <param name="symmetry">Symmetry to apply (e.g., "C1", "C2", "D7", etc.), or null for no symmetry.</param>
    /// <remarks>
    /// Creates visualizations showing:
    /// 1. The distribution of particle orientations on a unit sphere
    /// 2. The completeness of Fourier space sampling based on those orientations
    /// 
    /// These plots help assess whether the dataset has preferred orientations
    /// or missing views that might affect reconstruction quality.
    /// </remarks>
    public static void OrientationAndFourierSamplingHexBin(
        string particlesFile,
        string outputOrientationFile = null,
        string outputFourierSamplingFile = null,
        string symmetry = null
    )
    {
        var command = "orientation-and-fourier-sampling " +
                      $"--particles-file {particlesFile} " +
                      "--input-type relion_star_file " +
                      "--grid-resolution 2 " +
                      "--colormap chrisluts:I_Orange ";

        if(!String.IsNullOrEmpty(symmetry))
            command += $"--symmetry {symmetry} ";

        if(!String.IsNullOrEmpty(outputOrientationFile))
            command += $"--output-orientation-file {outputOrientationFile} ";

        if(!String.IsNullOrEmpty(outputFourierSamplingFile))
            command += $"--output-fourier-sampling-file {outputFourierSamplingFile} ";

        RunCommand(command);
    }

    /// <summary>
    /// Generates per-class orientation distribution and Fourier sampling completeness visualizations for 3D classification results.
    /// </summary>
    /// <param name="particlesFile">Path to the input particle STAR file from Class3D job.</param>
    /// <param name="nClasses">Number of 3D classes in the classification.</param>
    /// <param name="healpixOrder">HEALPix order for angular sampling (0-10), higher values produce finer sampling.</param>
    /// <param name="outputOrientationFile">Base path for the orientation distribution plots (class-specific suffixes will be added).</param>
    /// <param name="outputFourierSamplingFile">Base path for the Fourier sampling plots (class-specific suffixes will be added).</param>
    /// <param name="symmetry">Symmetry to apply (e.g., "C1", "C2", "D7", etc.), or null for no symmetry.</param>
    /// <remarks>
    /// Similar to <see cref="OrientationAndFourierSamplingHexBin"/>, but generates separate visualizations for each 3D class.
    /// Output filenames will include class suffixes (e.g., orientations.png -> orientations_class003.png).
    /// The healpixOrder parameter is converted to H3 grid resolution using an internal mapping.
    /// </remarks>
    public static void OrientationAndFourierSamplingHexBinClass3D(
        string particlesFile,
        int nClasses,
        int healpixOrder,
        string outputOrientationFile = null,
        string outputFourierSamplingFile = null,
        string symmetry = null
    )
    {
        /* Output filenames per class will include a `_class{class_number:03d)` suffix
         * e.g.
         * - my_orientations.png -> my_orientations_class003.png
         * - my_fourier_sampling.png -> my_fourier_sampling_class003.png
         */
        var healpixToH3 = new Dictionary<int, int>();
        healpixToH3.Add(0, 0);
        healpixToH3.Add(1, 0);
        healpixToH3.Add(2, 0);
        healpixToH3.Add(3, 1);
        healpixToH3.Add(4, 2);
        healpixToH3.Add(5, 2);
        healpixToH3.Add(6, 2);
        healpixToH3.Add(7, 2);
        healpixToH3.Add(8, 2);
        healpixToH3.Add(9, 2);
        healpixToH3.Add(10, 2);

        var command = "orientation-and-fourier-sampling-class3d " +
                      $"--particle-star-file {particlesFile} " +
                      $"--n-classes {nClasses} " +
                      $"--grid-resolution {healpixToH3[healpixOrder]} " +
                      "--colormap chrisluts:I_Orange ";

        if(!String.IsNullOrEmpty(symmetry))
            command += $"--symmetry {symmetry} ";

        if(!String.IsNullOrEmpty(outputOrientationFile))
            command += $"--output-orientation-file {outputOrientationFile} ";

        if(!String.IsNullOrEmpty(outputFourierSamplingFile))
            command += $"--output-fourier-sampling-file {outputFourierSamplingFile} ";

        RunCommand(command);
    }

    /// <summary>
    /// Creates an atlas of particle images from one or more STAR files.
    /// </summary>
    /// <param name="workingDirectory">Working directory for the command execution, typically where particle images are located.</param>
    /// <param name="particleStarFiles">Array of paths to STAR files containing particle metadata.</param>
    /// <param name="nImages">Number of particle images to include in the atlas.</param>
    /// <param name="outputImageFile">Path where the output atlas image will be saved.</param>
    /// <remarks>
    /// Generates a grid visualization of randomly selected particle images from the provided STAR files.
    /// Useful for visually inspecting the quality and appearance of extracted particles.
    /// </remarks>
    public static void ParticleImageAtlas(
        string workingDirectory,
        string[] particleStarFiles,
        int nImages,
        string outputImageFile
    )
    {
        var command = "particle-image-atlas " +
                      $"--n-images {nImages} " +
                      $"--output-file \"{outputImageFile}\" ";

        foreach (var file in particleStarFiles)
            command += $"--input-star-file \"{file}\" ";

        RunCommand(command, workingDirectory);
    }

    /// <summary>
    /// Creates an atlas of 2D class averages from a RELION Class2D job.
    /// </summary>
    /// <param name="class2dMrcsFile">Path to the MRC stack file containing 2D class averages.</param>
    /// <param name="outputImageFile">Path where the output atlas image will be saved.</param>
    /// <remarks>
    /// Generates a grid visualization of all 2D class averages from a RELION 2D classification job.
    /// Useful for visually inspecting the quality and variety of 2D class averages.
    /// </remarks>
    public static void Class2DImageAtlas(
        string class2dMrcsFile,
        string outputImageFile
    )
    {
        var command = "class2d-image-atlas " +
                      $"--images-mrcs-file {class2dMrcsFile} " +
                      $"--output-file \"{outputImageFile}\" ";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for a Class2D job with specific class averages.
    /// </summary>
    /// <param name="classImagesMrcsFile">Path to the MRC stack file containing 2D class averages.</param>
    /// <param name="imageIndices">Array of indices (0-based) of class averages to include in the visualization.</param>
    /// <param name="imageLabels">Array of labels to display for each class average.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Creates a visualization summarizing a 2D classification job, highlighting specific class averages
    /// with custom labels. The number of indices and labels must match.
    /// 
    /// Used in both Class2D and Class2DSelect jobs to generate summary visualizations for job cards:
    /// - In Class2D, it's used to create a compact visualization showing all class averages for each iteration
    /// - In Class2DSelect, it's specifically used to visualize the selected classes that will be exported
    ///   for downstream processing
    /// 
    /// The visualization serves as the primary thumbnail representation of classification results in the
    /// workflow graph, allowing users to quickly assess the quality and variety of class averages.
    /// </remarks>
    public static void Class2DJobCard(
        string classImagesMrcsFile,
        int[] imageIndices,  // 0-indexed
        string[] imageLabels,
        string outputImageFile
    )
    {

        // generate command
        var command = "class2d-job-card " +
                      $"--images-mrcs-file {classImagesMrcsFile} " +
                      $"--output-file \"{outputImageFile}\" ";
        
        for (int i = 0; i < imageIndices.Length; i++)
        {
            command += $"--idx {imageIndices[i].ToString()} ";
            command += $"--label {imageLabels[i]} ";
        }
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for motion correction and CTF estimation results.
    /// </summary>
    /// <param name="motionTracksJsonFile">Path to the motion tracks JSON file.</param>
    /// <param name="motionCorrectedImageMrcFile">Path to the motion-corrected micrograph MRC file.</param>
    /// <param name="frameSeriesProcessingXmlFile">Path to the frame series processing XML file containing CTF information.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization summarizing motion correction and CTF estimation results for a micrograph,
    /// typically showing the motion trajectory, motion-corrected image, power spectrum, and CTF fit.
    /// </remarks>
    public static void MotionAndCTF2DJobCard(
        string motionTracksJsonFile,
        string motionCorrectedImageMrcFile,
        string frameSeriesProcessingXmlFile,
        string outputImageFile
    )
    {
        var command = "motion-and-ctf-job-card " +
                      $"--motion-tracks-json-file {motionTracksJsonFile} " +
                      $"--motion-corrected-image-file {motionCorrectedImageMrcFile} " +
                      $"--frame-series-xml-file {frameSeriesProcessingXmlFile} " +
                      $"--output-file {outputImageFile}";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for particle picking results from BoxNet.
    /// </summary>
    /// <param name="motionCorrectedImageMrcFile1">Path to the first motion-corrected micrograph MRC file.</param>
    /// <param name="ParticleStarFile1">Path to the STAR file containing particle coordinates for the first micrograph.</param>
    /// <param name="motionCorrectedImageMrcFile2">Path to the second motion-corrected micrograph MRC file.</param>
    /// <param name="ParticleStarFile2">Path to the STAR file containing particle coordinates for the second micrograph.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization comparing particle picking results on two micrographs,
    /// showing the original micrographs with particle coordinates overlaid.
    /// </remarks>
    public static void BoxNetInference2DJobCard(
        string motionCorrectedImageMrcFile1,
        string ParticleStarFile1,
        string motionCorrectedImageMrcFile2,
        string ParticleStarFile2,
        string outputImageFile
    )
    {
        var command = "boxnet-inference-2d-job-card " +
                      $"--motion-corrected-image-file-1 {motionCorrectedImageMrcFile1} " +
                      $"--particle-star-file-1 {ParticleStarFile1} " +
                      $"--motion-corrected-image-file-2 {motionCorrectedImageMrcFile2} " +
                      $"--particle-star-file-2 {ParticleStarFile2} " +
                      $"--output-file {outputImageFile}";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for initial 3D reference volumes.
    /// </summary>
    /// <param name="volumeFiles">Array of paths to 3D volume files representing initial references.</param>
    /// <param name="classNumbers">Array of class numbers corresponding to each volume.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <exception cref="Exception">Thrown if the number of volume files doesn't match the number of class numbers.</exception>
    /// <remarks>
    /// Generates a visualization summarizing initial 3D reference volumes used for 3D classification or refinement.
    /// Each volume is displayed with its corresponding class number.
    /// </remarks>
    public static void InitialReference3DJobCard(
        string[] volumeFiles,
        int[] classNumbers,
        string outputImageFile
    )
    {
        if(classNumbers.Length != volumeFiles.Length)
            throw new Exception("need same number of class numbers as volume files");
        
        var command = "initial-reference-3d-job-card " +
                      $"--output-file {outputImageFile} ";

        for (int i = 0; i < volumeFiles.Length; i++)
        {
            command += $"--volume-file {volumeFiles[i]} ";
            command += $"--class-number {classNumbers[i].ToString()} ";
        }

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for 3D classification results.
    /// </summary>
    /// <param name="volumeFiles">Array of paths to 3D volume files representing class averages.</param>
    /// <param name="classNumbers">Array of class numbers corresponding to each volume.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <exception cref="Exception">Thrown if the number of volume files doesn't match the number of class numbers.</exception>
    /// <remarks>
    /// Generates a visualization summarizing 3D classification results, showing each 3D class with its class number.
    /// Similar to <see cref="InitialReference3DJobCard"/> but formats the visualization for classification results.
    /// </remarks>
    public static void Class3DJobCard(
        string[] volumeFiles,
        int[] classNumbers,
        string outputImageFile
    )
    {
        if(classNumbers.Length != volumeFiles.Length)
            throw new Exception("need same number of class numbers as volume files");
        
        var command = "class3d-job-card " +
                      $"--output-file {outputImageFile} ";

        for (int i = 0; i < volumeFiles.Length; i++)
        {
            command += $"--volume-file {volumeFiles[i]} ";
            command += $"--class-number {classNumbers[i].ToString()} ";
        }

        RunCommand(command);
    }
    
    /// <summary>
    /// Creates a job card visualization for 3D post-processing results.
    /// </summary>
    /// <param name="volumeFile">Path to the post-processed 3D volume file.</param>
    /// <param name="postProcessStarFile">Path to the STAR file containing post-processing metadata.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization summarizing 3D post-processing results, typically showing
    /// the final density map, FSC curve, and other quality metrics.
    /// </remarks>
    public static void PostProcess3DJobCard(
        string volumeFile,
        string postProcessStarFile,
        string outputImageFile
    )
    {
        var command = "postprocess3d-job-card " +
                      $"--volume-file {volumeFile} " +
                      $"--postprocess-star-file {postProcessStarFile} " +
                      $"--output-file {outputImageFile} ";

        RunCommand(command);
    }
    
    /// <summary>
    /// Creates a job card visualization for 3D refinement results.
    /// </summary>
    /// <param name="volumeFile">Path to the refined 3D volume file.</param>
    /// <param name="modelStarFile">Path to the STAR file containing refinement model parameters.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization summarizing 3D refinement results, typically showing
    /// the refined density map, FSC curve, and angular distribution.
    /// </remarks>
    public static void Refine3DJobCard(
        string volumeFile,
        string modelStarFile,
        string outputImageFile
    )
    {
        var command = "refine3d-job-card " +
                      $"--volume-file {volumeFile} " +
                      $"--model-star-file {modelStarFile} " +
                      $"--output-file {outputImageFile} ";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for an imported 3D map.
    /// </summary>
    /// <param name="volumeFile">Path to the imported 3D volume file.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization showing orthogonal slices through an imported 3D map.
    /// </remarks>
    public static void ImportMapJobCard(
        string volumeFile,
        string outputImageFile
    )
    {
        var command = "import-map-3d-job-card " +
                      $"--volume-file {volumeFile} " +
                      $"--output-file {outputImageFile} ";
        
        RunCommand(command);
    }
    
    /// <summary>
    /// Creates a job card visualization for imported particle sets.
    /// </summary>
    /// <param name="particleStarFiles">Array of paths to STAR files containing particle metadata.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <param name="workingDirectory">Working directory for the command execution, typically where particle images are located.</param>
    /// <remarks>
    /// Generates a visualization summarizing imported particle datasets, typically showing
    /// example particles and dataset statistics.
    /// </remarks>
    public static void ImportParticlesJobCard(
        string[] particleStarFiles,
        string outputImageFile,
        string workingDirectory
    )
    {
        var command = "import-particles-job-card " +
                      $"--output-file {outputImageFile} ";
        
        foreach (var file in particleStarFiles)
            command += $"--particle-star-file \"{file}\" ";
        
        RunCommand(command, workingDirectory: workingDirectory);
    }
    
    public static void TsEtomoJobCard(
        string tiltImagePngFile,
        int tiltImageIndex,
        string fiducialModelFile,
        string processedItemsJsonFile,
        string outputImageFile
    )
    {
        var command = "ts-etomo-job-card " +
                      $"--tilt-image-file {tiltImagePngFile} " +
                      $"--tilt-image-index {tiltImageIndex} " +
                      $"--fiducial-model-file {fiducialModelFile} " + 
                      $"--processed-items-json-file {processedItemsJsonFile} " + 
                      $"--output-file {outputImageFile} ";
        
        RunCommand(command);
    }
    
    public static void TsCtfJobCard(
        string tiltSeriesXmlfile,
        string processedItemsJsonFile,
        string outputImageFile
    )
    {
        var command = "ts-ctf-job-card " +
                      $"--tilt-series-xml-file {tiltSeriesXmlfile} " +
                      $"--processed-items-json-file {processedItemsJsonFile} " + 
                      $"--output-file {outputImageFile} ";
        
        RunCommand(command);
    }
    
    /// <summary>
    /// Creates a job card visualization for imported frame series data.
    /// </summary>
    /// <param name="stackFile1">Path to the first image stack file (MRC, MRCS, TIF, TIFF, or EER).</param>
    /// <param name="stackFile2">Path to the second image stack file (MRC, MRCS, TIF, TIFF, or EER).</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization showing averaged images from two frame series stacks side by side.
    /// The stacks are averaged along the z-axis and displayed using consistent normalization.
    /// Supports multiple image formats including MRC, MRCS, TIF, TIFF, and EER.
    /// </remarks>
    public static void ImportFsJobCard(
        string stackFile1,
        string stackFile2,
        string outputImageFile
    )
    {
        var command = "import-fs-job-card " +
                      $"--stack-file-1 {stackFile1} " +
                      $"--stack-file-2 {stackFile2} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for ts-reconstruct results.
    /// </summary>
    /// <param name="pngFile1">Path to the first PNG thumbnail file.</param>
    /// <param name="pngFile2">Path to the second PNG thumbnail file.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization showing two PNG thumbnail images side by side.
    /// The thumbnails are expected to be pre-generated by an external process and displayed as-is.
    /// </remarks>
    public static void TsReconstructJobCard(
        string pngFile1,
        string pngFile2,
        string outputImageFile
    )
    {
        var command = "ts-reconstruct-job-card " +
                      $"--png-file-1 {pngFile1} " +
                      $"--png-file-2 {pngFile2} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a ts-ctf card view with PNG thumbnail and CTF plot.
    /// </summary>
    /// <param name="pngFile">Path to the PNG thumbnail file.</param>
    /// <param name="tiltSeriesXmlFile">Path to the tilt series XML file.</param>
    /// <param name="outputImageFile">Path where the output card view image will be saved.</param>
    /// <remarks>
    /// Generates a card view showing a PNG thumbnail and CTF plot side by side.
    /// The thumbnail is displayed as-is, while the CTF plot is generated from the tilt series XML file.
    /// </remarks>
    public static void TsCtfCardView(
        string pngFile,
        string tiltSeriesXmlFile,
        string outputImageFile
    )
    {
        var command = "ts-ctf-card-view " +
                      $"--png-file {pngFile} " +
                      $"--tilt-series-xml-file {tiltSeriesXmlFile} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for particle selection results with two tomogram slices and particle positions.
    /// </summary>
    /// <param name="mrcFile1">Path to the first tomogram MRC file.</param>
    /// <param name="starFile1">Path to the first STAR file containing particle coordinates.</param>
    /// <param name="mrcFile2">Path to the second tomogram MRC file.</param>
    /// <param name="starFile2">Path to the second STAR file containing particle coordinates.</param>
    /// <param name="particleDiameterAngstroms">Particle diameter in angstroms.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a card view showing two tomogram slices with particle positions overlaid as yellow circles.
    /// The tomograms are averaged over central XY slices and normalized before particle positions are plotted.
    /// Particle coordinates are read from RELION STAR files and scaled according to the tomogram voxel size.
    /// </remarks>
    public static void TsSelectParticlesJobCard(
        string mrcFile1,
        string starFile1,
        string mrcFile2,
        string starFile2,
        float particleDiameterAngstroms,
        string outputImageFile
    )
    {
        var command = "ts-select-particles-job-card " +
                      $"--mrc-file-1 {mrcFile1} " +
                      $"--star-file-1 {starFile1} " +
                      $"--mrc-file-2 {mrcFile2} " +
                      $"--star-file-2 {starFile2} " +
                      $"--particle-diameter-angstroms {particleDiameterAngstroms} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for template matching with tomogram slice and template.
    /// </summary>
    /// <param name="tomogramMrcFile">Path to the tomogram MRC file.</param>
    /// <param name="starFile">Path to the STAR file containing particle coordinates.</param>
    /// <param name="templateMrcFile">Path to the template MRC file.</param>
    /// <param name="particleDiameterAngstroms">Particle diameter in angstroms.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a card view showing a tomogram slice with particle positions (left) and the template with diameter circle (right).
    /// The tomogram is averaged over central XY slices based on particle diameter and normalized before particle positions are plotted.
    /// The template shows its central XY slice with a yellow circle indicating the particle diameter used for matching.
    /// </remarks>
    public static void TsTemplateMatchJobCard(
        string tomogramMrcFile,
        string starFile,
        string templateMrcFile,
        float particleDiameterAngstroms,
        string outputImageFile
    )
    {
        var command = "ts-template-match-job-card " +
                      $"--tomogram-mrc-file {tomogramMrcFile} " +
                      $"--star-file {starFile} " +
                      $"--template-mrc-file {templateMrcFile} " +
                      $"--particle-diameter-angstroms {particleDiameterAngstroms} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for particle export results with two MRC files and particle circles.
    /// </summary>
    /// <param name="mrcFile1">Path to the first MRC file.</param>
    /// <param name="mrcFile2">Path to the second MRC file.</param>
    /// <param name="pixelSize">Pixel size in angstroms.</param>
    /// <param name="particleDiameterAngstroms">Particle diameter in angstroms.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a card view showing central slices of two MRC files with particle circles overlaid.
    /// Both MRC files are expected to have the same pixel size. The particle diameter is used to
    /// calculate the circle radius for visualization overlay on both images.
    /// </remarks>
    public static void TsExportParticlesJobCard(
        string mrcFile1,
        string mrcFile2,
        float pixelSize,
        float particleDiameterAngstroms,
        string outputImageFile
    )
    {
        var command = "ts-export-particles-job-card " +
                      $"--mrc-file-1 {mrcFile1} " +
                      $"--mrc-file-2 {mrcFile2} " +
                      $"--pixel-size {pixelSize} " +
                      $"--particle-diameter {particleDiameterAngstroms} " +
                      $"--output-file {outputImageFile}";
        
        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for M-species refinement results.
    /// </summary>
    /// <param name="volumeFile">Path to the refined 3D volume file.</param>
    /// <param name="modelStarFile">Path to the STAR file containing M-species model parameters.</param>
    /// <param name="speciesXmlFile">Path to the XML file containing species parameters including GlobalResolution.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Generates a visualization summarizing M-species refinement results, showing
    /// the refined density map and FSC curves (unmasked and randomized) using M-species column names.
    /// The STAR file contains a single unnamed table with FSC data, and the XML file provides
    /// the GlobalResolution value for display.
    /// </remarks>
    public static void MSpeciesJobCard(
        string volumeFile,
        string modelStarFile,
        string speciesXmlFile,
        string outputImageFile
    )
    {
        var command = "m-species-job-card " +
                      $"--volume-file {volumeFile} " +
                      $"--model-star-file {modelStarFile} " +
                      $"--species-xml-file {speciesXmlFile} " +
                      $"--output-file {outputImageFile} ";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for M-refine results with variable number of species.
    /// </summary>
    /// <param name="speciesFolder">Path to the folder containing species subfolders.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Handles variable number of species visualization:
    /// - Single species: Shows map slice and FSC curves (like m-species-job-card)
    /// - Multiple species: Shows only xy slices with resolution labels in top-right corner
    /// 
    /// Each species subfolder should contain:
    /// - {species_name}_denoised.mrc: Volume file for xy slice
    /// - {species_name}_fsc.star: FSC data (for single species mode)
    /// - {species_name}.species: XML file with GlobalResolution parameter
    /// 
    /// Follows class3d-job-card sizing logic with maximum 20 species.
    /// </remarks>
    public static void MRefineJobCard(
        string speciesFolder,
        string outputImageFile
    )
    {
        var command = "m-refine-job-card " +
                      $"--species-folder {speciesFolder} " +
                      $"--output-file {outputImageFile} ";

        RunCommand(command);
    }

    /// <summary>
    /// Creates a job card visualization for M Get Species results, reusing the code from m-refine-job-card.
    /// </summary>
    /// <param name="speciesFolder">Path to the folder containing species subfolders.</param>
    /// <param name="speciesName">Name of the specific species to visualize.</param>
    /// <param name="outputImageFile">Path where the output job card image will be saved.</param>
    /// <remarks>
    /// Creates a visualization for a single specified species, showing map slice and FSC curves 
    /// (like m-species-job-card format). The species subfolder should contain:
    /// - {species_name}_denoised.mrc: Volume file for xy slice
    /// - {species_name}_fsc.star: FSC data
    /// - {species_name}.species: XML file with GlobalResolution parameter
    /// </remarks>
    public static void MGetSpeciesJobCard(
        string speciesFolder,
        string speciesName,
        string outputImageFile
    )
    {
        var command = "m-refine-job-card " +
                      $"--species-folder {speciesFolder} " +
                      $"--species {speciesName} " +
                      $"--output-file {outputImageFile} ";

        RunCommand(command);
    }
}