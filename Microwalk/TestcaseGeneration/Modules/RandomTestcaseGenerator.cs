using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.Utilities;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration.Modules
{
    [FrameworkModule("random", "Generates random byte arrays of a given length.")]
    internal class RandomTestcaseGenerator : TestcaseStage
    {
        /// <summary>
        /// The amount of test cases to generate.
        /// </summary>
        private int _testcaseCount;

        /// <summary>
        /// The length of the single test cases.
        /// </summary>
        private int _testcaseLength;

        /// <summary>
        /// The test case output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory = null!;

        /// <summary>
        /// The number of the next test case.
        /// </summary>
        private int _nextTestcaseNumber = 0;

        /// <summary>
        /// The used random number generator.
        /// </summary>
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        /// <summary>
        /// Already generated test cases.
        /// </summary>
        private readonly HashSet<byte[]> _knownTestcases = new(new ByteArrayComparer());

        protected override async Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Parse options
            _testcaseCount = moduleOptions.GetChildNodeWithKey("amount")?.GetNodeInteger() ?? throw new ConfigurationException("Missing test case count.");
            _testcaseLength = moduleOptions.GetChildNodeWithKey("length")?.GetNodeInteger() ?? throw new ConfigurationException("Missing test case length.");

            // Sanity check
            const double warnPercentage = 0.95;
            if(Math.Ceiling(Math.Log2(_testcaseCount)) >= 8 * _testcaseLength * warnPercentage)
                await Logger.LogWarningAsync("The requested number of test cases is near to the maximum possible number of possible test cases.\n" +
                                             "Consider increasing test case length or decreasing test case count to avoid performance hits and a possible endless loop.");

            // Make sure output directory exists
            var outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing output directory.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();
        }

        public override async Task<TraceEntity> NextTestcaseAsync(CancellationToken token)
        {
            // Generate random bytes
            byte[] random = new byte[_testcaseLength];
            do
                _rng.GetBytes(random);
            while(_knownTestcases.Contains(random));

            // Remember test case
            _knownTestcases.Add(random);

            // Store test case
            string testcaseFileName = Path.Combine(_outputDirectory.FullName, $"{_nextTestcaseNumber}.testcase");
            await File.WriteAllBytesAsync(testcaseFileName, random, token);

            // Create trace entity object
            var traceEntity = new TraceEntity
            {
                Id = _nextTestcaseNumber,
                TestcaseFilePath = testcaseFileName
            };

            // Done
            await Logger.LogDebugAsync("Testcase #" + traceEntity.Id);
            ++_nextTestcaseNumber;
            return traceEntity;
        }

        public override Task<bool> IsDoneAsync()
        {
            return Task.FromResult(_nextTestcaseNumber >= _testcaseCount);
        }

        public override Task UnInitAsync()
        {
            // Cleanup
            _rng.Dispose();
            return Task.CompletedTask;
        }
    }
}