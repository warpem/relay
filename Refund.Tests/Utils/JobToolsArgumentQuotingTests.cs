using System.Collections.Generic;
using Refund.Utils;
using Xunit;

namespace Refund.Tests.Utils;

/// <summary>
/// Submission scripts are bash, so every argument value we interpolate is exposed to the
/// shell. An unquoted --input_pattern *.star gets globbed against the submitting directory
/// before WarpTools ever starts; when exactly one file matches, the pattern is silently
/// replaced and the tool searches for a filename that isn't there. Quoting has to stop that
/// without also killing $VAR expansion, which the templates rely on.
/// </summary>
public class JobToolsArgumentQuotingTests
{
    [Theory]
    [InlineData("*.star")]
    [InlineData("*_selected.star")]
    [InlineData("frame_???.mrc")]
    [InlineData("run_[12].star")]
    public void GlobCharactersAreProtectedFromTheShell(string pattern)
    {
        Assert.Equal($"\"{pattern}\"", JobTools.QuoteArgumentValue(pattern));
    }

    [Theory]
    [InlineData("$TMPDIR/work")]
    [InlineData("${TMPDIR}/work")]
    [InlineData("$HOME/relay/particles.star")]
    public void EnvironmentVariablesStayExpandable(string value)
    {
        // Double quotes, not single: bash still expands $VAR inside them.
        string Quoted = JobTools.QuoteArgumentValue(value);

        Assert.StartsWith("\"", Quoted);
        Assert.EndsWith("\"", Quoted);
        Assert.Contains(value, Quoted);
        Assert.DoesNotContain("\\$", Quoted);
    }

    [Fact]
    public void PathsWithSpacesSurviveAsOneArgument()
    {
        Assert.Equal("\"/my data/particles.star\"", JobTools.QuoteArgumentValue("/my data/particles.star"));
    }

    [Fact]
    public void EmbeddedQuotesAndBackslashesAreEscaped()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", JobTools.QuoteArgumentValue("say \"hi\""));
        Assert.Equal("\"a\\\\b\"", JobTools.QuoteArgumentValue("a\\b"));
    }

    [Fact]
    public void AlreadyQuotedValuesArePassedThrough()
    {
        Assert.Equal("\"*.star\"", JobTools.QuoteArgumentValue("\"*.star\""));
        Assert.Equal("\"\"", JobTools.QuoteArgumentValue("\"\""));
    }

    [Fact]
    public void EmptyValuesAreLeftAlone()
    {
        Assert.Equal("", JobTools.QuoteArgumentValue(""));
        Assert.Null(JobTools.QuoteArgumentValue(null));
    }

    [Fact]
    public void ComposeArgumentStringEmitsFlagsBareAndValuesQuoted()
    {
        var Arguments = new Dictionary<string, string>
        {
            ["settings"] = "277/processing.settings",
            ["2d"] = "",
            ["input_directory"] = "276/matching",
            ["input_pattern"] = "*.star",
            ["box"] = "64",
        };

        Assert.Equal("--settings \"277/processing.settings\" " +
                     "--2d " +
                     "--input_directory \"276/matching\" " +
                     "--input_pattern \"*.star\" " +
                     "--box \"64\"",
                     JobTools.ComposeArgumentString(Arguments));
    }
}
