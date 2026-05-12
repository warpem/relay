using System.Text.RegularExpressions;

namespace Refund.Utils;

public static class StringExtensions
{
    public static string ReplaceRegex(this string input, string pattern, string replacement)
    {
        return Regex.Replace(input, pattern, replacement);
    }
}