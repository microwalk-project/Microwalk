using System.IO;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("load", "Loads existing preprocessed traces from a given directory.")]
    internal class PreprocessedTraceLoader : PreprocessorStage
    {
        public override bool SupportsParallelism { get; } = false;

        private DirectoryInfo _inputDirectory = null!;
        private TracePrefixFile _tracePrefix = null!;

        protected override async Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Check input directory
            var inputDirectoryPath = moduleOptions.GetChildNodeWithKey("input-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing input directory.");
            _inputDirectory = new DirectoryInfo(inputDirectoryPath);
            if(!_inputDirectory.Exists)
                throw new ConfigurationException("Could not find input directory.");

            // Try to load prefix file
            string preprocessedPrefixFilePath = Path.Combine(_inputDirectory.FullName, "prefix.trace.preprocessed");
            if(!File.Exists(preprocessedPrefixFilePath))
            {
                await Logger.LogErrorAsync($"Could not find preprocessed trace prefix file.");
                throw new FileNotFoundException("Could not find preprocessed trace prefix file.", preprocessedPrefixFilePath);
            }

            var bytes = await File.ReadAllBytesAsync(preprocessedPrefixFilePath);
            _tracePrefix = new TracePrefixFile(bytes);
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }

        public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            // Try to deduce trace file from testcase ID
            string preprocessedTraceFilePath = Path.Combine(_inputDirectory.FullName, $"t{traceEntity.Id}.trace.preprocessed");
            if(!File.Exists(preprocessedTraceFilePath))
            {
                await Logger.LogErrorAsync($"Could not find preprocessed trace file for #{traceEntity.Id}.");
                throw new FileNotFoundException("Could not find preprocessed trace file.", preprocessedTraceFilePath);
            }

            traceEntity.PreprocessedTraceFilePath = preprocessedTraceFilePath;

            // Load trace
            var bytes = await File.ReadAllBytesAsync(preprocessedTraceFilePath);
            traceEntity.PreprocessedTraceFile = new TraceFile(_tracePrefix, bytes);
        }
    }
}