using System.Text.RegularExpressions;

namespace ClaudeHelpers;

/// <summary>
/// A simplified analyzer that uses regex pattern matching to find usages
/// </summary>
public class SimpleUsageAnalyzer
{
    private readonly string[] _targetProjects = { "Refund", "Relay", "Emoji", "RelaySourceGenerators" };
    private readonly string[] _excludedDirs = { "bin", "obj" };
    private readonly int _contextLinesBefore = 5;
    private readonly int _contextLinesAfter = 3;
    private readonly int _maxUsagesPerSymbol = 3;
    
    private readonly string _rootDir;
    
    public SimpleUsageAnalyzer(string rootDir)
    {
        _rootDir = rootDir;
    }
    
    public async Task AnalyzeFileAsync(string filePath)
    {
        Console.WriteLine($"Analyzing file: {filePath}");
        
        // Read the file
        string fileContent = await File.ReadAllTextAsync(filePath);
        
        // Extract class names, method names, and property names
        var symbols = ExtractSymbols(fileContent, filePath);
        
        if (symbols.Count == 0)
        {
            Console.WriteLine("No symbols found in file");
            return;
        }
        
        Console.WriteLine($"Found {symbols.Count} symbols: {string.Join(", ", symbols.Select(s => s.Name))}");
        
        // Find usages of each symbol
        var allUsages = new List<SymbolUsageData>();
        
        foreach (var symbol in symbols)
        {
            var usages = await FindUsagesAsync(symbol);
            if (usages.Usages.Count > 0)
            {
                allUsages.Add(usages);
            }
        }
        
        if (allUsages.Count > 0)
        {
            // Save the results
            string outputPath = $"{filePath}.usage";
            await SaveResultsAsync(filePath, allUsages, outputPath);
            Console.WriteLine($"Results saved to: {outputPath}");
        }
        else
        {
            Console.WriteLine("No usages found");
        }
    }
    
    private List<Symbol> ExtractSymbols(string fileContent, string filePath)
    {
        var symbols = new List<Symbol>();
        
        // Simple regex to extract class names
        var classRegex = new Regex(@"(?:public|internal|private|protected)\s+(?:abstract|static|partial|sealed)?\s*class\s+(\w+)");
        var classMatches = classRegex.Matches(fileContent);
        
        foreach (Match match in classMatches)
        {
            symbols.Add(new Symbol
            {
                Name = match.Groups[1].Value,
                Type = "Class",
                SourceFile = filePath
            });
            
            // For each class, extract its public methods and properties
            string className = match.Groups[1].Value;
            
            // Extract public methods
            var methodRegex = new Regex($@"(?:public|internal)\s+(?:static|virtual|override|abstract)?\s+\w+\s+(\w+)\s*\(");
            var methodMatches = methodRegex.Matches(fileContent);
            
            foreach (Match methodMatch in methodMatches)
            {
                symbols.Add(new Symbol
                {
                    Name = methodMatch.Groups[1].Value,
                    Type = "Method",
                    SourceFile = filePath,
                    ContainingType = className
                });
            }
            
            // Extract public properties
            var propertyRegex = new Regex($@"(?:public|internal)\s+(?:virtual|override|abstract)?\s+\w+\s+(\w+)\s*\{{");
            var propertyMatches = propertyRegex.Matches(fileContent);
            
            foreach (Match propertyMatch in propertyMatches)
            {
                symbols.Add(new Symbol
                {
                    Name = propertyMatch.Groups[1].Value,
                    Type = "Property",
                    SourceFile = filePath,
                    ContainingType = className
                });
            }
        }
        
        // Extract interface names
        var interfaceRegex = new Regex(@"(?:public|internal|private|protected)\s+interface\s+(\w+)");
        var interfaceMatches = interfaceRegex.Matches(fileContent);
        
        foreach (Match match in interfaceMatches)
        {
            symbols.Add(new Symbol
            {
                Name = match.Groups[1].Value,
                Type = "Interface",
                SourceFile = filePath
            });
        }
        
        return symbols;
    }
    
    private async Task<SymbolUsageData> FindUsagesAsync(Symbol symbol)
    {
        var usageData = new SymbolUsageData
        {
            SymbolName = symbol.Name,
            SymbolKind = symbol.Type,
            SymbolContainingType = symbol.ContainingType ?? "N/A",
            Usages = new List<UsageContext>()
        };
        
        Console.WriteLine($"Looking for usages of {symbol.Type} '{symbol.Name}'");
        
        // Get all C# and Razor files in target projects, excluding the source file
        var files = GetTargetFiles(symbol.SourceFile);
        
        // Find occurrences of the symbol in each file
        foreach (var file in files)
        {
            try
            {
                var usages = await FindSymbolInFileAsync(symbol, file);
                usageData.Usages.AddRange(usages);
                
                if (usageData.Usages.Count >= _maxUsagesPerSymbol)
                {
                    usageData.Usages = usageData.Usages.Take(_maxUsagesPerSymbol).ToList();
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file {file}: {ex.Message}");
            }
        }
        
        usageData.Usages = MergeOverlappingUsages(usageData.Usages);
        
        Console.WriteLine($"Found {usageData.Usages.Count} usages for {symbol.Name}");
        return usageData;
    }
    
    private List<string> GetTargetFiles(string excludeFile)
    {
        var files = new List<string>();
        
        foreach (var project in _targetProjects)
        {
            string projectDir = Path.Combine(_rootDir, project);
            if (!Directory.Exists(projectDir)) continue;
            
            // Get all C# and Razor files in the project
            string[] csharpFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);
            string[] razorFiles = Directory.GetFiles(projectDir, "*.razor", SearchOption.AllDirectories);
            
            // Combine and filter
            files.AddRange(csharpFiles.Concat(razorFiles)
                .Where(f => f != excludeFile && !_excludedDirs.Any(d => f.Contains($"{Path.DirectorySeparatorChar}{d}{Path.DirectorySeparatorChar}"))));
        }
        
        return files;
    }
    
    private async Task<List<UsageContext>> FindSymbolInFileAsync(Symbol symbol, string filePath)
    {
        var usages = new List<UsageContext>();
        
        string fileName = Path.GetFileName(filePath);
        string fileContent = await File.ReadAllTextAsync(filePath);
        var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        
        // Regex pattern depends on the symbol type
        string pattern = symbol.Type switch
        {
            "Class" => $@"\b{Regex.Escape(symbol.Name)}\b(?:\s*\.|:|\(|\s)(?!\s*=)",
            "Interface" => $@"\b{Regex.Escape(symbol.Name)}\b(?:\s*\.|:|\<|\>|\(|\s)(?!\s*=)",
            "Method" => $@"\.{Regex.Escape(symbol.Name)}\s*\(",
            "Property" => $@"\.{Regex.Escape(symbol.Name)}\b(?!\s*\()",
            _ => $@"\b{Regex.Escape(symbol.Name)}\b"
        };
        
        var regex = new Regex(pattern);
        
        for (int i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                int lineNumber = i + 1; // 1-based line numbers
                
                // Extract context
                int startLine = Math.Max(0, i - _contextLinesBefore);
                int endLine = Math.Min(lines.Length - 1, i + _contextLinesAfter);
                
                var contextLines = new List<string>();
                for (int j = startLine; j <= endLine; j++)
                {
                    contextLines.Add(lines[j]);
                }
                
                usages.Add(new UsageContext
                {
                    FilePath = filePath,
                    StartLine = startLine + 1, // 1-based line numbers
                    EndLine = endLine + 1,
                    UsageLine = lineNumber,
                    ContextText = string.Join(Environment.NewLine, contextLines)
                });
            }
        }
        
        return usages;
    }
    
    private List<UsageContext> MergeOverlappingUsages(List<UsageContext> usages)
    {
        // Group by file
        var usagesByFile = usages
            .GroupBy(u => u.FilePath)
            .ToDictionary(g => g.Key, g => g.OrderBy(u => u.StartLine).ToList());
            
        var mergedUsages = new List<UsageContext>();
        
        foreach (var fileUsages in usagesByFile.Values)
        {
            var mergedFileUsages = new List<UsageContext>();
            
            foreach (var usage in fileUsages)
            {
                var merged = false;
                
                for (int i = 0; i < mergedFileUsages.Count; i++)
                {
                    var existing = mergedFileUsages[i];
                    
                    // Check if they overlap or are close enough to merge
                    if (usage.StartLine <= existing.EndLine + 3 && usage.EndLine + 3 >= existing.StartLine)
                    {
                        // Merge them
                        int newStartLine = Math.Min(existing.StartLine, usage.StartLine);
                        int newEndLine = Math.Max(existing.EndLine, usage.EndLine);
                        
                        // Get the merged text
                        string fileContent = File.ReadAllText(usage.FilePath);
                        var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                        
                        var contextLines = new List<string>();
                        for (int j = newStartLine - 1; j < newEndLine; j++) // Convert to 0-based index
                        {
                            if (j >= 0 && j < lines.Length)
                            {
                                contextLines.Add(lines[j]);
                            }
                        }
                        
                        // Update the existing usage
                        mergedFileUsages[i] = new UsageContext
                        {
                            FilePath = existing.FilePath,
                            StartLine = newStartLine,
                            EndLine = newEndLine,
                            UsageLine = existing.UsageLine, // Keep the first usage line
                            ContextText = string.Join(Environment.NewLine, contextLines)
                        };
                        
                        merged = true;
                        break;
                    }
                }
                
                if (!merged)
                {
                    mergedFileUsages.Add(usage);
                }
            }
            
            mergedUsages.AddRange(mergedFileUsages);
        }
        
        return mergedUsages;
    }
    
    private async Task SaveResultsAsync(string sourceFilePath, List<SymbolUsageData> usages, string outputPath)
    {
        var reporter = new UsageReporter();
        await reporter.GenerateUsageReportAsync(sourceFilePath, usages, outputPath);
    }
}

public class Symbol
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Class, Method, Property, Interface
    public string SourceFile { get; set; } = string.Empty;
    public string? ContainingType { get; set; }
}