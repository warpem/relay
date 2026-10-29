using System.Text;

namespace Refund.Utils;

/// <summary>Command-line helpers for Beam cluster jobs.</summary>
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
}
