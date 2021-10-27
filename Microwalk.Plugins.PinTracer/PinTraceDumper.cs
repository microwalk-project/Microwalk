using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Plugins.PinTracer
{
    [FrameworkModule("pin-dump", "Dumps raw Pin trace files in a human-readable form.")]
    public class PinTraceDumper : PreprocessorStage
    {
        /// <summary>
        /// The trace dump output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory = null!;

        public override bool SupportsParallelism => true;

        public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            // Input check
            if(traceEntity.RawTraceFilePath == null)
                throw new Exception("Raw trace file path is null. Is the trace stage missing?");

            // Output file
            string outputFileName = Path.Combine(_outputDirectory.FullName, Path.GetFileName(traceEntity.RawTraceFilePath) + ".txt");
            await using var outputStream = File.Open(outputFileName, FileMode.Create);
            await using var outputWriter = new StreamWriter(outputStream);

            // Base path of raw trace files
            string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath) ?? throw new Exception($"Could not determine directory: {traceEntity.RawTraceFilePath}");

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
                        case PinTracePreprocessor.RawTraceEntryTypes.HeapAllocSizeParameter:
                        {
                            outputWriter.WriteLine("AllocSize: " + ((uint)rawTraceEntry.Param1).ToString("X8"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.HeapAllocAddressReturn:
                        {
                            outputWriter.WriteLine("AllocReturn: " + rawTraceEntry.Param2.ToString("X16"));
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.HeapFreeAddressParameter:
                        {
                            outputWriter.WriteLine("HeapFree: " + rawTraceEntry.Param2.ToString("X16"));
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

                            bool taken = (flags & PinTracePreprocessor.RawTraceBranchEntryFlags.Taken) != 0;

                            var rawBranchType = flags & PinTracePreprocessor.RawTraceBranchEntryFlags.BranchEntryTypeMask;
                            if(rawBranchType == PinTracePreprocessor.RawTraceBranchEntryFlags.Jump)
                                outputWriter.WriteLine("Jump: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else if(rawBranchType == PinTracePreprocessor.RawTraceBranchEntryFlags.Call)
                                outputWriter.WriteLine("Call: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else if(rawBranchType == PinTracePreprocessor.RawTraceBranchEntryFlags.Return)
                                outputWriter.WriteLine("Return: " + rawTraceEntry.Param1.ToString("X16") + " -> " + rawTraceEntry.Param2.ToString("X16") + (taken ? " [taken]" : " [not taken]"));
                            else
                            {
                                Logger.LogErrorAsync($"Unspecified instruction type on branch {rawTraceEntry.Param1:X16} -> {rawTraceEntry.Param2:X16}, skipping").Wait();
                            }

                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.MemoryRead:
                        {
                            outputWriter.WriteLine("MemoryRead: " + rawTraceEntry.Param1.ToString("X16") + " reads " + rawTraceEntry.Param2.ToString("X16") + " (" + rawTraceEntry.Param0 + " bytes)");
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.MemoryWrite:
                        {
                            outputWriter.WriteLine("MemoryWrite: " + rawTraceEntry.Param1.ToString("X16") + " writes " + rawTraceEntry.Param2.ToString("X16") + " (" + rawTraceEntry.Param0 + " bytes)");
                            break;
                        }

                        case PinTracePreprocessor.RawTraceEntryTypes.StackPointerModification:
                        {
                            var flags = (PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags)rawTraceEntry.Flag;

                            var instructionType = flags & PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.InstructionTypeMask;
                            if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Call)
                                outputWriter.WriteLine("StackMod: " + rawTraceEntry.Param1.ToString("X16") + " sets RSP = " + rawTraceEntry.Param2.ToString("X16") + " (call)");
                            else if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Return)
                                outputWriter.WriteLine("StackMod: " + rawTraceEntry.Param1.ToString("X16") + " sets RSP = " + rawTraceEntry.Param2.ToString("X16") + " (ret)");
                            else if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Other)
                                outputWriter.WriteLine("StackMod: " + rawTraceEntry.Param1.ToString("X16") + " sets RSP = " + rawTraceEntry.Param2.ToString("X16") + " (other)");
                            else
                            {
                                Logger.LogErrorAsync("Unspecified instruction type on stack pointer modification, skipping").Wait();
                            }

                            break;
                        }
                    }
                }
        }

        protected override Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing output directory.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            return Task.CompletedTask;
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }
    }
}