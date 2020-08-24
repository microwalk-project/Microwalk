using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.Extensions;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    [FrameworkModule("pin", "Generates traces using a Pin tool.")]
    internal class PinTraceGenerator : TraceStage
    {
        /// <summary>
        /// The trace output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// The Pin tool process handle.
        /// </summary>
        private Process _pinToolProcess;

        public override bool SupportsParallelism => false;

        public override async Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            // Debug
            await Logger.LogDebugAsync("Trace #" + traceEntity.Id);

            // Send test case
            await _pinToolProcess.StandardInput.WriteLineAsync($"t {traceEntity.Id}");
            await _pinToolProcess.StandardInput.WriteLineAsync(traceEntity.TestcaseFilePath);
            while(true)
            {
                // Read Pin tool output
                await Logger.LogDebugAsync("Read from Pin tool stdout...");
                string pinToolOutput = await _pinToolProcess.StandardOutput.ReadLineAsync();
                if(pinToolOutput == null)
                    throw new IOException("Could not read from Pin tool standard output (null). Probably the process has exited early.");

                // Parse output
                await Logger.LogDebugAsync($"Pin tool output: {pinToolOutput}");
                string[] outputParts = pinToolOutput.Split('\t');
                if(outputParts[0] == "t")
                {
                    // Store trace file name
                    traceEntity.RawTraceFilePath = outputParts[1];
                    break;
                }

                await Logger.LogWarningAsync("Unexpected message from Pin tool.\nPlease make sure that the investigated program does not print anything on stdout, since this might interfere with the Pin tool's output pipe.");
            }
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract mandatory configuration values
            string pinToolPath = moduleOptions.GetChildNodeWithKey("pin-tool-path").GetNodeString();
            string wrapperPath = moduleOptions.GetChildNodeWithKey("wrapper-path").GetNodeString();
            _outputDirectory = new DirectoryInfo(moduleOptions.GetChildNodeWithKey("output-directory").GetNodeString());
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();
            var imagesNode = moduleOptions.GetChildNodeWithKey("images");
            if(imagesNode == null || !(imagesNode is YamlSequenceNode imagesListNode))
                throw new ConfigurationException("No images list specified.");
            string imagesList = string.Join(':', imagesListNode.Children.Select(c => c.GetNodeString()));

            // Extract optional configuration values
            string pinPath = moduleOptions.GetChildNodeWithKey("pin-path")?.GetNodeString() ?? "pin";
            ulong? fixedRdrand = moduleOptions.GetChildNodeWithKey("rdrand")?.GetNodeUnsignedLongHex();
            int cpuModelId = moduleOptions.GetChildNodeWithKey("cpu")?.GetNodeInteger() ?? 0;

            // Prepare argument list
            var pinArgs = new List<string>
            {
                "-t", $"{pinToolPath}",
                "-o",
                $"{Path.GetFullPath(_outputDirectory.FullName) + Path.DirectorySeparatorChar} ", // The trailing space is required on Windows: Pin's command line parser else believes that the final backslash is an escape character
                "-i", $"{imagesList}"
            };
            if(fixedRdrand != null)
            {
                pinArgs.Add("-r");
                pinArgs.Add($"{fixedRdrand.Value}");
            }

            pinArgs.Add("-c");
            pinArgs.Add($"{cpuModelId}");
            pinArgs.Add("--");
            pinArgs.Add(wrapperPath);

            // Prepare Pin tool process
            await Logger.LogDebugAsync("Starting Pin tool process");
            ProcessStartInfo pinToolProcessStartInfo = new ProcessStartInfo
            {
                Arguments = string.Empty,
                FileName = pinPath,
                WorkingDirectory = _outputDirectory.FullName, // Places pin.log at the trace directory
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            pinToolProcessStartInfo.ArgumentList.AddRange(pinArgs);
            pinToolProcessStartInfo.EnvironmentVariables["PATH"] += Path.PathSeparator + Path.GetDirectoryName(wrapperPath);
            await Logger.LogDebugAsync($"Pin tool command: {pinToolProcessStartInfo.FileName} {string.Join(" ", pinToolProcessStartInfo.ArgumentList)}");

            // Start Pin tool
            _pinToolProcess = Process.Start(pinToolProcessStartInfo);
            if(_pinToolProcess == null)
            {
                await Logger.LogErrorAsync("Could not start the Pin process.");
                return;
            }

            // Read and log error output of Pin tool (avoids pipe contention leading to I/O hangs)
            _pinToolProcess.ErrorDataReceived += async (sender, e) => await Logger.LogDebugAsync($"Pin tool log: {e.Data}");
            _pinToolProcess.BeginErrorReadLine();
        }

        public override async Task UninitAsync()
        {
            // Exit Pin tool process
            await Logger.LogDebugAsync("Stopping Pin tool process");
            _pinToolProcess.StandardInput.WriteLine("e 0");
            await _pinToolProcess.WaitForExitAsync();
        }
    }
}