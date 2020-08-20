using System.IO;
using System.Threading.Tasks;
using Microwalk.Extensions;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("load", "Loads existing preprocessed traces from a given directory.")]
    internal class PreprocessedTraceLoader : PreprocessorStage
    {
        public override bool SupportsParallelism { get; } = false;

        private DirectoryInfo _inputDirectory;
        private TracePrefixFile _tracePrefix;

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Check input directory
            _inputDirectory = new DirectoryInfo(moduleOptions.GetChildNodeWithKey("input-directory").GetNodeString());
            if(!_inputDirectory.Exists)
                throw new ConfigurationException("Could not find input directory.");

            // Try to load prefix file
            string preprocessedPrefixFilePath = Path.Combine(_inputDirectory.FullName, $"prefix.trace.preprocessed");
            if(!File.Exists(preprocessedPrefixFilePath))
            {
                await Logger.LogErrorAsync($"Could not find preprocessed trace prefix file.");
                throw new FileNotFoundException("Could not find preprocessed trace prefix file.", preprocessedPrefixFilePath);
            }

            using var reader = new FastBinaryReader(preprocessedPrefixFilePath);
            _tracePrefix = new TracePrefixFile(reader);
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
            using var reader = new FastBinaryReader(preprocessedTraceFilePath);
            traceEntity.PreprocessedTraceFile = new TraceFile(_tracePrefix, reader);
        }
    }
}