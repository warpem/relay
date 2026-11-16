using System.Text.RegularExpressions;
using Xunit;

namespace Refund.Tests.Components;

/// <summary>
/// Razor treats "@" right after a letter or digit as part of an email address, so "v@Session.Model" renders
/// literally. Such expressions need parentheses: "v@(Session.Model)".
/// </summary>
public class RazorEmailLiteralTests
{
    [Fact]
    public void NoRazorExpressionIsGluedToPrecedingText()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Refund", "Jobs")))
            root = root.Parent;
        var glued = new Regex(@"[A-Za-z0-9]@[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z]");
        var offenders = new[] { "Refund", "Relay", "ComponentDev" }
            .Select(folder => Path.Combine(root!.FullName, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.razor", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, index, line)))
            .Where(entry => glued.IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(root!.FullName, entry.path)}:{entry.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Razor renders these as text: " + string.Join(", ", offenders));
    }
}
