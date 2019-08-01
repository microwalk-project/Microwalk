using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk
{
    internal class CommandLineOptions
    {
        [Value(0, MetaName = "configuration-file", Required = false, Default = null, HelpText = "The framework configuration file.")]
        public string ConfigurationFile { get; set; }
    }
}
