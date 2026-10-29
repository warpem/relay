using System.Diagnostics;
using System.Text;

namespace Refund.Utils;

/// <summary>Local metadata conversion; refinement runs through the normal cluster queue.</summary>
public static class BeamWrapper
{
    internal static Dictionary<string, string> ParseArguments(string arguments)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool escaped = false, started = false;
        foreach (char c in arguments ?? "")
        {
            if (escaped) { token.Append(c); escaped = false; started = true; }
            else if (c == '\\' && quote != '\'') { escaped = true; started = true; }
            else if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else token.Append(c);
            }
            else if (c is '\'' or '"') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(c); started = true; }
        }
        if (escaped || quote != '\0') throw new ArgumentException("Additional arguments contain an unfinished escape or quote.");
        if (started) tokens.Add(token.ToString());
        var result = new Dictionary<string, string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string option = tokens[i];
            if (!option.StartsWith("--") || option.Length == 2)
                throw new ArgumentException($"Expected a --option, got '{option}'.");
            int equals = option.IndexOf('=');
            string name = equals < 0 ? option[2..] : option[2..equals];
            result[name] = equals >= 0 ? option[(equals + 1)..] :
                i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--") ? tokens[++i] : "";
        }
        return result;
    }

    internal static ProcessStartInfo ImportStartInfo(IEnumerable<string> metadataPaths, string projectRoot, string output)
    {
        var info = new ProcessStartInfo("beam")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "import", "--project-root", Path.GetFullPath(projectRoot),
                     "--output", Path.GetFullPath(output), "--absolute-paths", "--check-stacks", "--input" })
            info.ArgumentList.Add(arg);
        foreach (string path in metadataPaths)
            info.ArgumentList.Add(path);
        return info;
    }

    public static void ImportParticles(IEnumerable<string> metadataPaths, string projectRoot, string output,
        TextWriter logger, string errorPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = ImportStartInfo(metadataPaths, projectRoot, output) };
        process.Start();
        using var registration = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        logger.Write(stdout.GetAwaiter().GetResult());
        string errors = stderr.GetAwaiter().GetResult();
        File.WriteAllText(errorPath, errors);
        logger.Flush();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Beam particle import failed (exit code {process.ExitCode}): {errors.Trim()}");
    }
}
