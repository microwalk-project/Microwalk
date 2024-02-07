using System.Collections.Generic;
using CommandLine;

namespace Microwalk;

internal class CommandLineOptions
{
    [Value(0, MetaName = "configuration-file", Required = false, Default = null, HelpText = "The framework configuration file.")]
    public string? ConfigurationFile { get; set; }

    [Option('p', "plugin-directories", Required = false, Default = null, HelpText = "Specify plugin directories.")]
    public IEnumerable<string>? PluginDirectories { get; set; }
}