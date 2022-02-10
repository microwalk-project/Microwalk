using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.Plugins.PinTracer.Extensions;

namespace Microwalk.Plugins.PinTracer
{
    [FrameworkModule("pin", "Generates traces using a Pin tool.")]
    public class PinTraceGenerator : TraceStage
    {
        private const string _genericLogMessagePrefix = "[trace:pin]";

        /// <summary>
        /// The trace output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory = null!;

        /// <summary>
        /// The Pin tool process handle.
        /// </summary>
        private Process _pinToolProcess = null!;

        // Not supported. This module only manages a single Pin instance, which currently is fast enough.
        public override bool SupportsParallelism => false;

        public override async Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            string logMessagePrefix = $"[trace:pin:{traceEntity.Id}]";
            
            // Debug
            await Logger.LogDebugAsync($"{logMessagePrefix} Trace #" + traceEntity.Id);

            // Send test case
            await _pinToolProcess.StandardInput.WriteLineAsync($"t {traceEntity.Id}");
            await _pinToolProcess.StandardInput.WriteLineAsync(traceEntity.TestcaseFilePath);
            while(true)
            {
                // Read Pin tool output
                await Logger.LogDebugAsync($"{logMessagePrefix} Read from Pin tool stdout...");
                string pinToolOutput = await _pinToolProcess.StandardOutput.ReadLineAsync()
                                       ?? throw new IOException("Could not read from Pin tool standard output (null). Probably the process has exited early.");

                // Parse output
                await Logger.LogDebugAsync($"{logMessagePrefix} Pin tool output: {pinToolOutput}");
                string[] outputParts = pinToolOutput.Split('\t');
                if(outputParts[0] == "t")
                {
                    // Store trace file name
                    traceEntity.RawTraceFilePath = outputParts[1];
                    break;
                }

                await Logger.LogWarningAsync($"{logMessagePrefix} Unexpected message from Pin tool.\nPlease make sure that the investigated program does not print anything on stdout, since this might interfere with the Pin tool's output pipe.");
            }
        }

        protected override async Task InitAsync(MappingNode? moduleOptions)
        {
            if(moduleOptions == null)
                throw new ConfigurationException("Missing module configuration.");

            // Extract mandatory configuration values
            string pinToolPath = moduleOptions.GetChildNodeOrDefault("pin-tool-path")?.AsString() ?? throw new ConfigurationException("Missing Pin tool path.");
            string wrapperPath = moduleOptions.GetChildNodeOrDefault("wrapper-path")?.AsString() ?? throw new ConfigurationException("Missing wrapper path.");

            // Check output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("Missing output directory.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Load image list
            var imagesNode = moduleOptions.GetChildNodeOrDefault("images");
            if(imagesNode is not ListNode imagesListNode)
                throw new ConfigurationException("No images list specified.");
            string imagesList = string.Join(':', imagesListNode.Children.Select(c => c.AsString()));

            // Extract optional configuration values
            string pinPath = moduleOptions.GetChildNodeOrDefault("pin-path")?.AsString() ?? "pin";
            ulong? fixedRdrand = moduleOptions.GetChildNodeOrDefault("rdrand")?.AsUnsignedLongHex();
            int cpuModelId = moduleOptions.GetChildNodeOrDefault("cpu")?.AsInteger() ?? 0;
            bool enableStackTracking = moduleOptions.GetChildNodeOrDefault("stack-tracking")?.AsBoolean() ?? false;

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

            if(enableStackTracking)
            {
                pinArgs.Add("-s");
                pinArgs.Add("1");
            }

            pinArgs.Add("-c");
            pinArgs.Add($"{cpuModelId}");
            pinArgs.Add("--");
            pinArgs.Add(wrapperPath);

            // Prepare Pin tool process
            await Logger.LogDebugAsync($"{_genericLogMessagePrefix} Starting Pin tool process");
            ProcessStartInfo pinToolProcessStartInfo = new()
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

            // Environment variables
            if(moduleOptions.GetChildNodeOrDefault("environment") is MappingNode environmentNode)
            {
                foreach(var variable in environmentNode.Children)
                {
                    string value = variable.Value.AsString() ?? throw new ConfigurationException($"Invalid value for environment variable '{variable.Key}'");
                    pinToolProcessStartInfo.EnvironmentVariables[variable.Key] = value;
                }
            }

            pinToolProcessStartInfo.EnvironmentVariables["PATH"] += Path.PathSeparator + Path.GetDirectoryName(wrapperPath);

            // Start Pin tool
            await Logger.LogDebugAsync($"{_genericLogMessagePrefix} Pin tool command: {pinToolProcessStartInfo.FileName} {string.Join(" ", pinToolProcessStartInfo.ArgumentList)}");
            _pinToolProcess = Process.Start(pinToolProcessStartInfo) ?? throw new Exception("Could not start the Pin process.");

            // Ensure that the Pin process is eventually stopped when the Pipeline gets aborted early
            PipelineToken.Register(() =>
            {
                if(_pinToolProcess.HasExited)
                    return;

                try
                {
                    // Try to stop the Pin process the clean way
                    _pinToolProcess.StandardInput.WriteLineAsync("e 0").Wait(1000);
                    if(_pinToolProcess.WaitForExit(1000))
                        return;

                    _pinToolProcess.Kill(true);

                    if(!_pinToolProcess.WaitForExit(1000))
                        Logger.LogErrorAsync($"{_genericLogMessagePrefix} Sent a KILL signal to the Pin tool process, but it did not respond in time. Please check whether it still running.").Wait(2000);
                }
                catch(Exception ex)
                {
                    Logger.LogErrorAsync($"{_genericLogMessagePrefix} Could not safely stop the Pin tool process. Please check whether it still running. Error message:\n{ex}").Wait(2000);
                }
            });

            // Read and log error output of Pin tool (avoids pipe contention leading to I/O hangs)
            _pinToolProcess.ErrorDataReceived += async (_, e) =>
            {
                if(!string.IsNullOrWhiteSpace(e.Data))
                    await Logger.LogDebugAsync($"{_genericLogMessagePrefix} Pin tool log: {e.Data}");
            };
            _pinToolProcess.BeginErrorReadLine();
        }

        public override async Task UnInitAsync()
        {
            // Exit Pin tool process
            await Logger.LogDebugAsync($"{_genericLogMessagePrefix} Stopping Pin tool process");
            if(!_pinToolProcess.HasExited)
            {
                await _pinToolProcess.StandardInput.WriteLineAsync("e 0");
                await _pinToolProcess.WaitForExitAsync();
            }
        }
    }
}