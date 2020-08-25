using Microwalk.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("pin-dump", "Dumps raw Pin trace files in a human-readable form.")]
    internal class PinTraceDumper : PreprocessorStage
    {
        /// <summary>
        /// The trace dump output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        public override bool SupportsParallelism => true;

        public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            // Output file
            string outputFileName = Path.Combine(_outputDirectory.FullName, Path.GetFileName(traceEntity.RawTraceFilePath) + ".txt");
            await using var outputStream = File.OpenWrite(outputFileName);
            await using var outputWriter = new StreamWriter(outputStream);

            // Base path of raw trace files
            string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath);

            // Write image data
            string prefixDataFilePath = Path.Combine(rawTraceFileDirectory!, "prefix_data.txt"); // Suppress "possible null" warning
            await outputWriter.WriteLineAsync("-- Image data --");
            await outputWriter.WriteLineAsync(await File.ReadAllTextAsync(prefixDataFilePath));

            // Write prefix
            await outputWriter.WriteLineAsync("-- Trace prefix --");
            DumpRawFile(Path.Combine(rawTraceFileDirectory, "prefix.trace"), outputWriter);

            // Write trace
            await outputWriter.WriteLineAsync("-- Trace --");
            DumpRawFile(traceEntity.RawTraceFilePath, outputWriter);
        }

        /// <summary>
        /// Loads the given raw trace and converts it into text format.
        /// </summary>
        /// <param name="fileName">Raw trace file.</param>
        /// <param name="outputWriter">Output stream writer.</param>
        /// <returns></returns>
        private unsafe void DumpRawFile(string fileName, StreamWriter outputWriter)
        {
            // Read entire trace file into memory
            byte[] inputFile = File.ReadAllBytes(fileName);
            int inputFileLength = inputFile.Length;
            int rawTraceEntrySize = Marshal.SizeOf(typeof(PinTracePreprocessor.RawTraceEntry));

            // Dump trace entries
            fixed(byte* inputFilePtr = inputFile)
                for(long pos = 0; pos < inputFileLength; pos += rawTraceEntrySize)
                {
                    // Read entry
                    var rawTraceEntry = *(PinTracePreprocessor.RawTraceEntry*)&inputFilePtr[pos];

                    // Write string representation
                    switch(rawTraceEntry.Type)
                    {
                        case PinTracePreprocessor.RawTraceEntryTypes.AllocSizeParameter:
                        {
                            outputWriter.WriteLine("AllocSize: " + ((uint)rawTraceEntry.Param1).ToString("X8"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.AllocAddressReturn:
                        {
                            outputWriter.WriteLine("AllocReturn: " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.FreeAddressParameter:
                        {
                            outputWriter.WriteLine("Free: " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.StackPointerInfo:
                        {
                            outputWriter.WriteLine("StackPtr: " + rawTraceEntry.Param1.ToString("X16") + " " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.Branch:
                        {
                            var flags = (PinTracePreprocessor.RawTraceBranchEntryFlags)rawTraceEntry.Flag;
                            bool taken = flags.HasFlag(PinTracePreprocessor.RawTraceBranchEntryFlags.Taken);
                            if(flags.HasFlag(PinTracePreprocessor.RawTraceBranchEntryFlags.Jump))
                                outputWriter.WriteLine("Jump: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else if(flags.HasFlag(PinTracePreprocessor.RawTraceBranchEntryFlags.Call))
                                outputWriter.WriteLine("Call: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else if(flags.HasFlag(PinTracePreprocessor.RawTraceBranchEntryFlags.Return))
                                outputWriter.WriteLine("Return: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else
                            {
                                Logger.LogErrorAsync($"Unspecified instruction type on branch {rawTraceEntry.Param1:X16} -> {rawTraceEntry.Param2:X16}, skipping").Wait();
                                break;
                            }

                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.MemoryRead:
                        {
                            outputWriter.WriteLine("MemoryRead: " + rawTraceEntry.Param1.ToString("X16") + " reads " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.MemoryWrite:
                        {
                            outputWriter.WriteLine("MemoryWrite: " + rawTraceEntry.Param1.ToString("X16") + " writes " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }
                    }
                }
        }

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath == null)
                throw new ConfigurationException("No output directory specified.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            return Task.CompletedTask;
        }
    }
}
