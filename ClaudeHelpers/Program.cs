using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace ClaudeHelpers;

public class Program
{
    private static readonly string[] TargetProjects = new[] { "Refund", "Relay", "Emoji", "RelaySourceGenerators" };
    private static readonly string[] ExcludedDirs = new[] { "bin", "obj" };
    
    public static async Task Main(string[] args)
    {
        string solutionPath = "/Users/tegunovd/dev/NewRelay/Relay.sln";
        string? singleFilePath = null;
        string? outputDir = null;
        bool processAllFiles = false;
        
        // Simple command line parsing
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--solution" && i + 1 < args.Length)
            {
                solutionPath = args[i + 1];
                i++;
            }
            else if (args[i] == "--file" && i + 1 < args.Length)
            {
                singleFilePath = args[i + 1];
                i++;
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputDir = args[i + 1];
                i++;
            }
            else if (args[i] == "--all")
            {
                processAllFiles = true;
            }
            else if (args[i].EndsWith(".sln"))
            {
                solutionPath = args[i];
            }
            else if ((args[i].EndsWith(".cs") || args[i].EndsWith(".razor")) && File.Exists(args[i]))
            {
                singleFilePath = args[i];
            }
        }
        
        Console.WriteLine($"Analyzing solution: {solutionPath}");
        
        if (singleFilePath != null)
        {
            Console.WriteLine($"Analyzing single file: {singleFilePath}");
            processAllFiles = false;
        }
        else if (processAllFiles)
        {
            Console.WriteLine("Processing all files in target projects");
        }
        else
        {
            Console.WriteLine("Error: No file specified. Use --file <path> to analyze a single file or --all to process all files.");
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run [--solution <path>] [--file <path> | --all] [--output <dir>]");
            return;
        }
        
        try
        {
            // Load the solution
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            
            // Process diagnostics
            bool hasFatalErrors = false;
            foreach (var diagnostic in workspace.Diagnostics)
            {
                Console.WriteLine($"{diagnostic.Kind}: {diagnostic.Message}");
                if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    hasFatalErrors = true;
                }
            }
            
            if (hasFatalErrors)
            {
                Console.WriteLine("Fatal errors occurred with Roslyn analyzer. Try using the source build of the project.");
                return;
            }
            
            // Filter to target projects
            var targetProjects = solution.Projects
                .Where(p => TargetProjects.Contains(p.Name))
                .ToList();
                
            Console.WriteLine($"Found {targetProjects.Count} target projects: {string.Join(", ", targetProjects.Select(p => p.Name))}");
            
            var symbolAnalyzer = new SymbolAnalyzer(solution);
            var reporter = new UsageReporter();
            
            var generatedFiles = new List<string>();
            
            if (singleFilePath != null)
            {
                // Process just the single file
                var result = await ProcessSingleFile(singleFilePath, targetProjects, symbolAnalyzer, reporter, outputDir);
                if (result != null) generatedFiles.Add(result);
            }
            else
            {
                // Process all files in target projects
                foreach (var project in targetProjects)
                {
                    Console.WriteLine($"Processing project: {project.Name}");
                    var results = await ProcessProject(project, symbolAnalyzer, reporter, outputDir);
                    generatedFiles.AddRange(results);
                }
            }
            
            Console.WriteLine($"Analysis complete. Generated {generatedFiles.Count} usage files.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
    
    private static async Task<string?> ProcessSingleFile(string filePath, List<Project> projects, SymbolAnalyzer analyzer, UsageReporter reporter, string? outputDir)
    {
        // Find the document in any of our target projects
        Document? targetDocument = null;
        foreach (var project in projects)
        {
            var documents = project.Documents.Where(d => d.FilePath == filePath).ToList();
            if (documents.Count > 0)
            {
                targetDocument = documents[0];
                Console.WriteLine($"Found file in project: {project.Name}");
                break;
            }
        }
        
        if (targetDocument == null)
        {
            Console.WriteLine($"Error: Could not find file '{filePath}' in any target project");
            return null;
        }
        
        // Process just this document
        return await ProcessDocument(targetDocument, analyzer, reporter, outputDir);
    }
    
    private static async Task<List<string>> ProcessProject(Project project, SymbolAnalyzer analyzer, UsageReporter reporter, string? outputDir)
    {
        var generatedFiles = new List<string>();
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            Console.WriteLine($"  Failed to get compilation for {project.Name}");
            return generatedFiles;
        }
        
        // Get all documents in the project that are not in excluded directories
        var documents = project.Documents
            .Where(d => !ExcludedDirs.Any(dir => d.FilePath?.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}") == true))
            .ToList();
            
        Console.WriteLine($"  Found {documents.Count} documents to analyze");
        
        // Process each document
        foreach (var document in documents)
        {
            var result = await ProcessDocument(document, analyzer, reporter, outputDir);
            if (result != null) generatedFiles.Add(result);
        }
        
        return generatedFiles;
    }
    
    private static async Task<string?> ProcessDocument(Document document, SymbolAnalyzer analyzer, UsageReporter reporter, string? outputDir)
    {
        Console.WriteLine($"  Analyzing {Path.GetFileName(document.FilePath ?? string.Empty)}");
        
        // Extract symbols from document
        var symbols = await analyzer.ExtractSymbolsFromDocumentAsync(document);
        
        if (symbols.Count == 0)
        {
            Console.WriteLine("    No symbols found in document");
            return null;
        }
        
        Console.WriteLine($"    Found {symbols.Count} symbols");
        
        // Find references for each symbol
        var usageData = await analyzer.FindReferencesForSymbolsAsync(symbols);
        
        // Generate usage report
        if (usageData.Any())
        {
            string originalPath = document.FilePath!;
            string outputPath;
            
            if (outputDir != null)
            {
                // Use custom output directory
                string relativePath = Path.GetRelativePath(analyzer.GetSolutionDirectory(), originalPath);
                outputPath = Path.Combine(outputDir, relativePath + ".usage");
                
                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            }
            else
            {
                // Save next to the original file
                outputPath = originalPath + ".usage";
            }
            
            await reporter.GenerateUsageReportAsync(originalPath, usageData, outputPath);
            Console.WriteLine($"    Generated usage report: {outputPath}");
            return outputPath;
        }
        else
        {
            Console.WriteLine("    No usages found for symbols in document");
            return null;
        }
    }
}