using CommandLine;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Relay.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppingUnloadableSpacesRequiresExplicitFlag(bool recover)
    {
        string[] args = recover ? ["--drop-unloadable-spaces"] : [];
        // ASP.NET also reads these arguments before the application CLI parser.
        _ = new ConfigurationBuilder().AddCommandLine(args).Build();
        using var parser = new Parser(settings => settings.HelpWriter = null);
        var result = parser.ParseArguments<Cli>(args);
        var options = Assert.IsType<Parsed<Cli>>(result).Value;
        Assert.Equal(recover, options.DropUnloadableSpaces);
    }
}
