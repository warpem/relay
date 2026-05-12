using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace ClaudeHelpers;

public class SymbolAnalyzer
{
    private readonly Solution _solution;
    private const int MaxUsagesPerSymbol = 3;
    private const int ContextLinesBefore = 5;
    private const int ContextLinesAfter = 3;
    
    /// <summary>
    /// Gets the solution directory path
    /// </summary>
    public string GetSolutionDirectory() => Path.GetDirectoryName(_solution.FilePath) ?? "";

    public SymbolAnalyzer(Solution solution)
    {
        _solution = solution;
    }

    public async Task<List<ISymbol>> ExtractSymbolsFromDocumentAsync(Document document)
    {
        var symbols = new List<ISymbol>();
        
        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            Console.WriteLine($"    Failed to get semantic model for {document.FilePath}");
            return symbols;
        }

        var syntaxRoot = await document.GetSyntaxRootAsync();
        if (syntaxRoot == null)
        {
            Console.WriteLine($"    Failed to get syntax root for {document.FilePath}");
            return symbols;
        }

        // Extract class declarations
        var classDeclarations = syntaxRoot.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var classDeclaration in classDeclarations)
        {
            var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration);
            if (classSymbol != null)
            {
                symbols.Add(classSymbol);

                // Add public properties
                foreach (var member in classSymbol.GetMembers())
                {
                    if (member.DeclaredAccessibility == Accessibility.Public && 
                        (member.Kind == SymbolKind.Property || member.Kind == SymbolKind.Method))
                    {
                        symbols.Add(member);
                    }
                }
            }
        }

        // Extract interface declarations
        var interfaceDeclarations = syntaxRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
        foreach (var interfaceDeclaration in interfaceDeclarations)
        {
            var interfaceSymbol = semanticModel.GetDeclaredSymbol(interfaceDeclaration);
            if (interfaceSymbol != null)
            {
                symbols.Add(interfaceSymbol);

                // Add public members
                foreach (var member in interfaceSymbol.GetMembers())
                {
                    if (member.DeclaredAccessibility == Accessibility.Public)
                    {
                        symbols.Add(member);
                    }
                }
            }
        }

        // Extract struct declarations
        var structDeclarations = syntaxRoot.DescendantNodes().OfType<StructDeclarationSyntax>();
        foreach (var structDeclaration in structDeclarations)
        {
            var structSymbol = semanticModel.GetDeclaredSymbol(structDeclaration);
            if (structSymbol != null)
            {
                symbols.Add(structSymbol);

                // Add public properties and methods
                foreach (var member in structSymbol.GetMembers())
                {
                    if (member.DeclaredAccessibility == Accessibility.Public &&
                        (member.Kind == SymbolKind.Property || member.Kind == SymbolKind.Method))
                    {
                        symbols.Add(member);
                    }
                }
            }
        }

        // Extract enum declarations
        var enumDeclarations = syntaxRoot.DescendantNodes().OfType<EnumDeclarationSyntax>();
        foreach (var enumDeclaration in enumDeclarations)
        {
            var enumSymbol = semanticModel.GetDeclaredSymbol(enumDeclaration);
            if (enumSymbol != null)
            {
                symbols.Add(enumSymbol);
            }
        }

        return symbols;
    }

    public async Task<List<SymbolUsageData>> FindReferencesForSymbolsAsync(List<ISymbol> symbols)
    {
        var usageDataList = new List<SymbolUsageData>();
        var processedSymbols = new HashSet<string>(); // Track already processed symbols by signature
        
        // Filter out accessor methods (getters/setters) as we'll handle them with the property
        symbols = symbols.Where(s => 
            !(s is IMethodSymbol method && 
              (method.MethodKind == MethodKind.PropertyGet || 
               method.MethodKind == MethodKind.PropertySet))).ToList();
        
        foreach (var symbol in symbols)
        {
            // Skip interface implementation members - they generate too many false positives
            if (symbol is IMethodSymbol methodSymbol && 
                methodSymbol.ContainingType != null && 
                methodSymbol.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers())
                    .Any(m => m.Name == methodSymbol.Name && methodSymbol.ExplicitInterfaceImplementations.IsEmpty))
            {
                // Check if method is a common interface implementation (like Dispose, GetEnumerator, etc.)
                var interfaceMembers = methodSymbol.ContainingType.AllInterfaces
                    .SelectMany(i => i.GetMembers())
                    .Where(m => m.Name == methodSymbol.Name);
                
                if (interfaceMembers.Any())
                {
                    Console.WriteLine($"    Skipping likely interface implementation: {methodSymbol.Name} in {methodSymbol.ContainingType.Name}");
                    continue;
                }
            }
            
            // Create unique signature to avoid processing the same symbol multiple times
            string signature = GetSymbolSignature(symbol);
            if (processedSymbols.Contains(signature))
            {
                continue;
            }
            processedSymbols.Add(signature);
            
            var usageData = new SymbolUsageData
            {
                SymbolName = symbol.Name,
                SymbolKind = symbol.Kind.ToString(),
                SymbolContainingType = symbol.ContainingType?.Name ?? "N/A",
                Usages = new List<UsageContext>()
            };

            var references = await SymbolFinder.FindReferencesAsync(symbol, _solution);
            var referenceLocations = new List<ReferenceLocation>();

            foreach (var reference in references)
            {
                // Skip references in the symbol's own definition document
                var definitionDoc = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath;
                
                foreach (var location in reference.Locations)
                {
                    // Skip references in the same document as the definition
                    if (location.Document.FilePath != definitionDoc)
                    {
                        // Skip generated files from Razor (.g.cs, etc.)
                        if (IsGeneratedFile(location.Document.FilePath))
                        {
                            continue;
                        }
                        
                        referenceLocations.Add(location);
                    }
                }
            }

            // Get the top N usages
            var topReferences = referenceLocations
                .OrderByDescending(loc => File.GetLastWriteTime(loc.Document.FilePath ?? string.Empty))
                .Take(MaxUsagesPerSymbol)
                .ToList();
            
            foreach (var location in topReferences)
            {
                var usage = await ExtractUsageContextAsync(location);
                if (usage != null)
                {
                    usageData.Usages.Add(usage);
                }
            }

            if (usageData.Usages.Count > 0)
            {
                usageDataList.Add(usageData);
            }
        }

        return MergeOverlappingUsages(usageDataList);
    }
    
    /// <summary>
    /// Generates a unique signature for a symbol to avoid duplicates
    /// </summary>
    private string GetSymbolSignature(ISymbol symbol)
    {
        string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
        return $"{containingType}.{symbol.Name}.{symbol.Kind}";
    }
    
    /// <summary>
    /// Determines if a file is likely generated code (e.g., from Razor)
    /// </summary>
    private bool IsGeneratedFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
            
        return filePath.Contains(".g.cs") || 
               filePath.Contains(".designer.cs") ||
               filePath.Contains("obj/Debug") || 
               filePath.Contains("obj/Release");
    }

    private async Task<UsageContext?> ExtractUsageContextAsync(ReferenceLocation location)
    {
        try
        {
            var document = location.Document;
            var sourceText = await document.GetTextAsync();
            var linePosition = location.Location.GetLineSpan();
            
            var usageLine = linePosition.StartLinePosition.Line;
            var startLine = Math.Max(0, usageLine - ContextLinesBefore);
            var endLine = Math.Min(sourceText.Lines.Count - 1, usageLine + ContextLinesAfter);
            
            var contextText = sourceText.GetSubText(
                new TextSpan(
                    sourceText.Lines[startLine].Start, 
                    sourceText.Lines[endLine].End - sourceText.Lines[startLine].Start
                )
            ).ToString();
            
            return new UsageContext
            {
                FilePath = document.FilePath ?? "Unknown",
                StartLine = startLine + 1, // 1-based line numbers for display
                EndLine = endLine + 1,
                UsageLine = usageLine + 1,
                ContextText = contextText
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Error extracting usage context: {ex.Message}");
            return null;
        }
    }

    private List<SymbolUsageData> MergeOverlappingUsages(List<SymbolUsageData> usageDataList)
    {
        // Group by symbols to maintain the structure
        return usageDataList.Select(symbolData =>
        {
            // Group usages by file
            var usagesByFile = symbolData.Usages
                .GroupBy(u => u.FilePath)
                .ToList();
                
            var mergedUsages = new List<UsageContext>();
            
            foreach (var fileGroup in usagesByFile)
            {
                var fileUsages = fileGroup.OrderBy(u => u.StartLine).ToList();
                var mergedFileUsages = new List<UsageContext>();
                
                // Process each file's usages
                for (int i = 0; i < fileUsages.Count; i++)
                {
                    var current = fileUsages[i];
                    var merged = false;
                    
                    // Try to merge with existing merged usages
                    for (int j = 0; j < mergedFileUsages.Count; j++)
                    {
                        var existing = mergedFileUsages[j];
                        
                        // Check if they overlap or are close enough to merge
                        if (current.StartLine <= existing.EndLine + 3 && current.EndLine + 3 >= existing.StartLine)
                        {
                            // Merge them
                            int newStartLine = Math.Min(existing.StartLine, current.StartLine);
                            int newEndLine = Math.Max(existing.EndLine, current.EndLine);
                            
                            // Get the merged text - we would need the full file content to do this properly
                            // Here we're approximating by taking the larger context
                            string newText = existing.ContextText.Length > current.ContextText.Length 
                                ? existing.ContextText 
                                : current.ContextText;
                                
                            // Update the existing usage
                            mergedFileUsages[j] = new UsageContext
                            {
                                FilePath = existing.FilePath,
                                StartLine = newStartLine,
                                EndLine = newEndLine,
                                UsageLine = existing.UsageLine, // Keep the first usage line
                                ContextText = newText
                            };
                            
                            merged = true;
                            break;
                        }
                    }
                    
                    // If not merged, add as a new usage
                    if (!merged)
                    {
                        mergedFileUsages.Add(current);
                    }
                }
                
                mergedUsages.AddRange(mergedFileUsages);
            }
            
            // Create a new SymbolUsageData with merged usages
            return new SymbolUsageData
            {
                SymbolName = symbolData.SymbolName,
                SymbolKind = symbolData.SymbolKind,
                SymbolContainingType = symbolData.SymbolContainingType,
                Usages = mergedUsages
            };
        }).ToList();
    }
}