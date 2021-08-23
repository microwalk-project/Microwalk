using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration.Modules
{
    [FrameworkModule("load", "Loads existing testcase files from a given directory.")]
    internal class TestcaseLoader : TestcaseStage
    {
        private Queue<string> _testcaseFileNames = null!;
        private int _nextTestcaseNumber = 0;

        protected override async Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Check input directory
            var inputDirectoryPath = moduleOptions.GetChildNodeWithKey("input-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing input directory.");
            var inputDirectory = new DirectoryInfo(inputDirectoryPath);
            if(!inputDirectory.Exists)
                throw new ConfigurationException("Could not find input directory.");

            // Read all testcase file names
            _testcaseFileNames = new Queue<string>(inputDirectory.EnumerateFiles("*.testcase", SearchOption.TopDirectoryOnly).Select(f => f.FullName));
            await Logger.LogInfoAsync($"Found {_testcaseFileNames.Count} testcase files.");
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }

        public override async Task<TraceEntity> NextTestcaseAsync(CancellationToken token)
        {
            // Create trace entity object
            var traceEntity = new TraceEntity
            {
                Id = _nextTestcaseNumber,
                TestcaseFilePath = _testcaseFileNames.Dequeue()
            };

            // Done
            await Logger.LogDebugAsync("Testcase #" + traceEntity.Id);
            ++_nextTestcaseNumber;
            return traceEntity;
        }

        public override Task<bool> IsDoneAsync()
        {
            return Task.FromResult(_testcaseFileNames.Count == 0);
        }
    }
}