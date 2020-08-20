using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.Extensions;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("load", "Loads existing trace files.")]
    internal class TraceLoader : TraceStage
    {
        DirectoryInfo _inputDirectory;
        public override bool SupportsParallelism { get; } = false;

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Check input directory
            _inputDirectory = new DirectoryInfo(moduleOptions.GetChildNodeWithKey("input-directory").GetNodeString());
            if(!_inputDirectory.Exists)
                throw new ConfigurationException("Could not find input directory.");

            return Task.CompletedTask;
        }

        public override async Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            // Try to deduce trace file from testcase ID
            string rawTraceFilePath = Path.Combine(_inputDirectory.FullName, $"t{traceEntity.Id}.trace");
            if(!File.Exists(rawTraceFilePath))
            {
                await Logger.LogErrorAsync($"Could not find raw trace file for #{traceEntity.Id}.");
                throw new FileNotFoundException("Could not find raw trace file.", rawTraceFilePath);
            }

            traceEntity.RawTraceFilePath = rawTraceFilePath;
        }
    }
}