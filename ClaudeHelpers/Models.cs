using System.Text.Json.Serialization;

namespace ClaudeHelpers;

/// <summary>
/// Represents usage information for a symbol
/// </summary>
public class SymbolUsageData
{
    /// <summary>
    /// The name of the symbol
    /// </summary>
    public string SymbolName { get; set; } = string.Empty;
    
    /// <summary>
    /// The kind of the symbol (Class, Method, Property, etc.)
    /// </summary>
    public string SymbolKind { get; set; } = string.Empty;
    
    /// <summary>
    /// The containing type of the symbol (if applicable)
    /// </summary>
    public string SymbolContainingType { get; set; } = string.Empty;
    
    /// <summary>
    /// List of usage contexts for this symbol
    /// </summary>
    public List<UsageContext> Usages { get; set; } = new();
}

/// <summary>
/// Represents a usage context for a symbol reference
/// </summary>
public class UsageContext
{
    /// <summary>
    /// The path to the file containing the usage
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
    
    /// <summary>
    /// The start line of the context (1-based)
    /// </summary>
    public int StartLine { get; set; }
    
    /// <summary>
    /// The end line of the context (1-based)
    /// </summary>
    public int EndLine { get; set; }
    
    /// <summary>
    /// The line containing the actual usage (1-based)
    /// </summary>
    public int UsageLine { get; set; }
    
    /// <summary>
    /// The text of the context
    /// </summary>
    public string ContextText { get; set; } = string.Empty;
}