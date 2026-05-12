using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaySourceGenerators;

[Generator]
public class EmojiGenerator : ISourceGenerator
{
    private static readonly char[] InvalidCharacters = new[] { '_', ' ', '-', '.', ',', '&', '’', '\'', ':', '(', ')', '“', '”', '"', '!' };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
    
    public void Initialize(GeneratorInitializationContext context) {}

    private static string ToPascalCase(string value)
    {
        return ToPascalCase(value.Split(InvalidCharacters, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ToPascalCase(string[] words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (word.Length > 0)
            {
                words[i] = char.ToUpper(word[0]) + word.Substring(1).ToLower();
            }
        }
        return string.Join("", words);
    }

    private static string GetFluentClassName(string cldr) =>
        ToPascalCase(cldr)
            .Replace("1st", "First")
            .Replace("2nd", "Second")
            .Replace("3rd", "third");

    private static string GetFluentGroupName(string group) =>
        ToPascalCase(group);

    public void Execute(GeneratorExecutionContext context)
    {
        if (!context.Compilation.AssemblyName.Equals("Emoji", StringComparison.OrdinalIgnoreCase))
            return;
        
        var metadataFiles = context.AdditionalFiles
            .Where(f => f.Path.EndsWith("metadata.json"));
            
        var emojis = new List<EmojiMetadata>();

        foreach (var file in metadataFiles)
        {
            var content = file.GetText()?.ToString();
            if (string.IsNullOrEmpty(content)) continue;

            try
            {
                var metadata = JsonSerializer.Deserialize<EmojiMetadata>(content, JsonOptions);
                if (metadata == null) continue;
                
                metadata.FluentClassName = GetFluentClassName(metadata.Cldr);
                metadata.FluentGroupName = GetFluentGroupName(metadata.Group);
                
                emojis.Add(metadata);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        var source = GenerateEmojiClass(emojis);
        context.AddSource("Emoji.g.cs", source);
    }

    private string GenerateEmojiClass(List<EmojiMetadata> emojis)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using Microsoft.FluentUI.AspNetCore.Components;");
        sb.AppendLine("using FEmoji = Microsoft.FluentUI.AspNetCore.Components.Emoji;");
        sb.AppendLine();
        sb.AppendLine("namespace Relay.Emoji;");
        sb.AppendLine();
        sb.AppendLine("public static partial class EmojiLibrary");
        sb.AppendLine("{");
        
        // Generate lookup dictionary by glyph
        sb.AppendLine("    public static readonly Dictionary<string, EmojiInfo> ByGlyph = new()");
        sb.AppendLine("    {");
        foreach (var emoji in emojis.OrderBy(e => e.Group).ThenBy(e => e.Unicode))
        {
            sb.AppendLine($"        [\"{emoji.Glyph}\"] = new EmojiInfo(");
            sb.AppendLine($"            \"{emoji.Cldr}\",");
            sb.AppendLine($"            \"{emoji.Glyph}\",");
            sb.AppendLine($"            \"{emoji.Group}\",");
            sb.AppendLine($"            \"{emoji.Unicode}\",");
            sb.AppendLine($"            new[] {{ {string.Join(", ", emoji.Keywords.Select(k => $"\"{k}\""))} }},");
            sb.AppendLine($"            {(emoji.HasSkinTones ? "true" : "false")},");
            sb.AppendLine($"            \"{emoji.FluentClassName}\",");
            sb.AppendLine($"            \"{emoji.FluentGroupName}\",");
            sb.AppendLine($"            EmojiInfo.GetFluentType(\"{emoji.FluentGroupName}\", \"{emoji.FluentClassName}\") != null ?");
            sb.AppendLine($"                (FEmoji)Activator.CreateInstance(EmojiInfo.GetFluentType(\"{emoji.FluentGroupName}\", \"{emoji.FluentClassName}\")) :"); 
            sb.AppendLine($"                null");
            sb.AppendLine("        ),");
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");

        return sb.ToString();
    }
}

public class EmojiMetadata
{
    public string Cldr { get; set; }
    public string Glyph { get; set; }
    public string Group { get; set; }
    public string[] Keywords { get; set; }
    public string[] UnicodeSkintones { get; set; }
    public string Unicode { get; set; }
    public bool HasSkinTones => UnicodeSkintones?.Length > 0;
    
    // FluentUI mapping info
    public string FluentClassName { get; set; }
    public string FluentGroupName { get; set; }
}