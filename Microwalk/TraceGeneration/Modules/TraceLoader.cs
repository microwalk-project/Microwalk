using System.IO;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("load", "Loads existing raw traces from a given directory.")]
    internal class TraceLoader : TraceStage
    {
        public override bool SupportsParallelism { get; } = false;
        
        private DirectoryInfo _inputDirectory = null!;

        protected override Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Check input directory
            var inputDirectoryPath = moduleOptions.GetChildNodeWithKey("input-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing input directory.");
            _inputDirectory = new DirectoryInfo(inputDirectoryPath);
            if(!_inputDirectory.Exists)
                throw new ConfigurationException("Could not find input directory.");

            return Task.CompletedTask;
        }

        public override Task UnInitAsync()
        {
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