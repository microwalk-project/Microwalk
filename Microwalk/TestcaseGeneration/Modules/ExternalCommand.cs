using Microsoft.VisualBasic;
using Microwalk.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration.Modules
{
    [FrameworkModule("command", "Calls an external application to generate test cases.")]
    internal class ExternalCommand : TestcaseStage
    {
        /// <summary>
        /// The amount of test cases to generate.
        /// </summary>
        private int _testcaseCount;

        /// <summary>
        /// The test case output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Path to target executable.
        /// </summary>
        private string _commandFilePath;

        /// <summary>
        /// The command argument format string.
        /// </summary>
        private string _argumentTemplate;

        /// <summary>
        /// The number of the next test case.
        /// </summary>
        private int _nextTestcaseNumber = 0;

        private string FormatCommand(int testcaseId, string testcaseFileName, string testcaseFilePath)
            => string.Format(_argumentTemplate, testcaseId, testcaseFileName, testcaseFilePath);

        public override Task<bool> IsDoneAsync()
        {
            return Task.FromResult(_nextTestcaseNumber >= _testcaseCount);
        }

        public override async Task<TraceEntity> NextTestcaseAsync(CancellationToken token)
        {
            // Format argument string
            string testcaseFileName = $"{_nextTestcaseNumber}.testcase";
            string testcaseFilePath = Path.Combine(_outputDirectory.FullName, testcaseFileName);
            string args = FormatCommand(_nextTestcaseNumber, testcaseFileName, testcaseFilePath);

            // Generate testcase
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                Arguments = args,
                FileName = _commandFilePath,
                WorkingDirectory = _outputDirectory.FullName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = Process.Start(processStartInfo);
            await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Create trace entity object
            var traceEntity = new TraceEntity
            {
                Id = _nextTestcaseNumber,
                TestcaseFilePath = testcaseFilePath
            };

            // Done
            await Logger.LogDebugAsync("Testcase #" + traceEntity.Id);
            ++_nextTestcaseNumber;
            return traceEntity;
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Parse options
            _testcaseCount = moduleOptions.GetChildNodeWithKey("amount").GetNodeInteger();
            _commandFilePath = moduleOptions.GetChildNodeWithKey("exe").GetNodeString();
            _argumentTemplate = moduleOptions.GetChildNodeWithKey("args").GetNodeString();

            // Make sure output directory exists
            _outputDirectory = new DirectoryInfo(moduleOptions.GetChildNodeWithKey("output-directory").GetNodeString());
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Print example command for debugging
            await Logger.LogDebugAsync("Loaded command based testcase generator. Example command: \n> "
                + _commandFilePath + " " + FormatCommand(0, "0.testcase", Path.Combine(_outputDirectory.FullName, "0.testcase")));
        }
    }
}
