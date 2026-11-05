using CommandLine;

namespace Relay;

/// <summary>
/// Command line interface options for Relay application.
/// Provides configuration through command-line arguments for customizing runtime behavior.
/// Parsed in Program.cs using CommandLineParser library with Parser.Default.ParseArguments.
/// </summary>
public class Cli
{
    [Option("drop-unloadable-spaces", Default = false,
        HelpText = "Start even if spaces cannot be loaded, removing them from their projects. Subsequent saves will persist these removals.")]
    public bool DropUnloadableSpaces { get; set; }
}
