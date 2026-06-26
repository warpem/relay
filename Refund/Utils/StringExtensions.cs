using System.Text.RegularExpressions;

namespace Refund.Utils;

public static class StringExtensions
{
    public static string ReplaceRegex(this string input, string pattern, string replacement)
    {
        // Insert the replacement literally. The single-string Regex.Replace overload treats the
        // replacement as a substitution template where "$$" means a literal "$", "${name}" is a
        // group reference, etc. — which silently corrupts shell content like "$$" (PID) or
        // "${VAR:-default}". Using a MatchEvaluator inserts the value verbatim.
        return Regex.Replace(input, pattern, _ => replacement);
    }
}