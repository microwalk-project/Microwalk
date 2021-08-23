using CommandLine;

namespace Microwalk
{
    internal class CommandLineOptions
    {
        [Value(0, MetaName = "configuration-file", Required = false, Default = null, HelpText = "The framework configuration file.")]
        public string? ConfigurationFile { get; set; }
        
        [Option('p', "plugin-directory", Required = false, Default = null, HelpText = "The plugin directory.")]
        public string? PluginDirectory { get; set; }
    }
}