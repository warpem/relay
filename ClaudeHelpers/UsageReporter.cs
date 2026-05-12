using System.Text;

namespace ClaudeHelpers;

public class UsageReporter
{
    public async Task GenerateUsageReportAsync(string sourceFilePath, List<SymbolUsageData> usageDataList, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"USAGE REPORT FOR: {sourceFilePath}");
            
            foreach (var symbolData in usageDataList)
            {
                string symbolType = symbolData.SymbolKind == "NamedType" ? "Type" : symbolData.SymbolKind;
                
                if (symbolData.SymbolContainingType != "N/A")
                {
                    sb.AppendLine($"\n{symbolType}: {symbolData.SymbolContainingType}.{symbolData.SymbolName}");
                }
                else
                {
                    sb.AppendLine($"\n{symbolType}: {symbolData.SymbolName}");
                }
                
                if (symbolData.Usages.Count == 0)
                {
                    sb.AppendLine("No usages found outside of definition file.");
                }
                else
                {
                    foreach (var usage in symbolData.Usages)
                    {
                        sb.AppendLine($"  In: {usage.FilePath} (Line {usage.UsageLine})");
                        
                        // Clean up the context text 
                        string cleanedContext = CleanContextText(usage.ContextText);
                        sb.AppendLine(cleanedContext);
                    }
                }
            }

            await File.WriteAllTextAsync(outputPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating usage report: {ex.Message}");
        }
    }
    
    private string CleanContextText(string contextText)
    {
        var lines = contextText.Split('\n');
        var cleanedLines = new List<string>();
        
        foreach (var line in lines)
        {
            string trimmedLine = line.TrimEnd();
            
            // Skip XML doc comments and empty lines
            if (trimmedLine.TrimStart().StartsWith("///") || string.IsNullOrWhiteSpace(trimmedLine))
                continue;
                
            // Keep regular comments and code, without extra padding
            cleanedLines.Add(trimmedLine);
        }
        
        return string.Join('\n', cleanedLines);
    }
}