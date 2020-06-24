using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microwalk.Extensions;
using Microwalk.TraceEntryTypes;
using Microwalk.Utilities;
using SauceControl.Blake2Fast;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("memory-access-mi", "Calculates the mutual information (MI) for each memory accessing instruction.")]
    internal class MemoryAccessMi : AnalysisStage
    {
        /// <summary>
        /// Maps testcase IDs to lists of instruction hashes (testcase ID => instruction address => hash).
        /// </summary>
        private readonly ConcurrentDictionary<int, Dictionary<ulong, byte[]>> _testcaseInstructionHashes = new ConcurrentDictionary<int, Dictionary<ulong, byte[]>>();

        /// <summary>
        /// The output directory for analysis results.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        public override bool SupportsParallelism => true;

        public override Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Allocate dictionary for mapping instruction addresses to memory access hashes
            Dictionary<ulong, byte[]> instructionHashes = new Dictionary<ulong, byte[]>();

            // Hash all memory access instructions
            foreach(var traceEntry in traceEntity.PreprocessedTraceFile.Entries)
            {
                // Extract instruction and memory address
                ulong instructionAddress;
                ulong memoryAddress;
                switch(traceEntry.EntryType)
                {
                    case TraceEntry.TraceEntryTypes.HeapMemoryAccess:
                    {
                        var heapMemoryAccess = (HeapMemoryAccess)traceEntry;
                        instructionAddress = ((ulong)heapMemoryAccess.InstructionImageId << 32) | heapMemoryAccess.InstructionRelativeAddress;
                        memoryAddress = ((ulong)heapMemoryAccess.MemoryAllocationBlockId << 32) | heapMemoryAccess.MemoryRelativeAddress;
                        break;
                    }
                    case TraceEntry.TraceEntryTypes.ImageMemoryAccess:
                    {
                        var imageMemoryAccess = (ImageMemoryAccess)traceEntry;
                        instructionAddress = ((ulong)imageMemoryAccess.InstructionImageId << 32) | imageMemoryAccess.InstructionRelativeAddress;
                        memoryAddress = ((ulong)imageMemoryAccess.MemoryImageId << 32) | imageMemoryAccess.MemoryRelativeAddress;
                        break;
                    }
                    case TraceEntry.TraceEntryTypes.StackMemoryAccess:
                    {
                        var stackMemoryAccess = (StackMemoryAccess)traceEntry;
                        instructionAddress = ((ulong)stackMemoryAccess.InstructionImageId << 32) | stackMemoryAccess.InstructionRelativeAddress;
                        memoryAddress = stackMemoryAccess.MemoryRelativeAddress;
                        break;
                    }
                    default:
                        continue;
                }

                // Update hash
                if(!instructionHashes.TryGetValue(instructionAddress, out var hash))
                {
                    hash = new byte[32];
                    instructionHashes.Add(instructionAddress, hash);
                }

                var hashSpan = hash.AsSpan();
                MemoryMarshal.Write(hashSpan, ref memoryAddress); // Will overwrite first 8 bytes of hash
                Blake2s.ComputeAndWriteHash(hashSpan, hashSpan);
            }

            // Store instruction hashes
            _testcaseInstructionHashes.AddOrUpdate(traceEntity.Id, instructionHashes, (t, h) => h);

            // Done
            return Task.CompletedTask;
        }

        public override async Task FinishAsync()
        {
            Dictionary<ulong, double> mutualInformationPerInstruction = new Dictionary<ulong, double>();
            unchecked
            {
                // Transform instruction hash lists into better usable form
                var instructions = new Dictionary<ulong, InstructionData>(); // Maps instruction addresses to testcase count and hash count
                foreach(var testcase in _testcaseInstructionHashes)
                {
                    // Iterate instructions in this testcase
                    foreach(var instruction in testcase.Value)
                    {
                        // Retrieve instruction data object
                        if(!instructions.TryGetValue(instruction.Key, out var instructionData))
                        {
                            instructionData = new InstructionData();
                            instructions.Add(instruction.Key, instructionData);
                        }

                        // Add data from this testcase
                        ++instructionData.TestcaseCount;
                        instructionData.HashCounts.TryGetValue(instruction.Value, out int hashCount); // Will be 0 if not existing
                        instructionData.HashCounts[instruction.Value] = hashCount + 1;
                    }
                }

                // Calculate mutual information of each instruction
                foreach(var instruction in instructions)
                {
                    // Calculate probabilities of keys, and keys with traces (if they caused a call of this instruction)
                    // Since the keys are uniquely generated, we have a uniform distribution
                    double pX = 1.0 / instruction.Value.TestcaseCount;
                    double pXy = 1.0 / instruction.Value.TestcaseCount;

                    // Calculate mutual information
                    double mutualInformation = 0.0;
                    foreach(var addressCount in instruction.Value.HashCounts)
                    {
                        double pY = (double)addressCount.Value / instruction.Value.TestcaseCount;
                        mutualInformation += addressCount.Value * pXy * Math.Log(pXy / (pX * pY), 2);
                    }

                    mutualInformationPerInstruction.Add(instruction.Key, mutualInformation);
                }
            }

            // Store results in single text file
            await Logger.LogInfoAsync("Mutual information analysis completed, writing results\n");
            await using var writer =
                new StreamWriter(File.Open(Path.Combine(_outputDirectory.FullName, "memory-access-mi.txt"), FileMode.Create, FileAccess.Write, FileShare.Read));

            // Sort instructions by information loss and output
            double maximumMutualInformation = 0.0;
            foreach(var instructionData in mutualInformationPerInstruction.OrderBy(mi => mi.Key).ThenByDescending(mi => mi.Value))
            {
                // Update maximum variable, so later a warning can be issued if there were not enough testcases
                if(instructionData.Value > maximumMutualInformation)
                    maximumMutualInformation = instructionData.Value;

                // Write result
                // TODO allow to convert instruction addresses into nice text form (including MAP file)
                await writer.WriteLineAsync($"Instruction 0x{instructionData.Key:X16}: {instructionData.Value.ToString("N3", CultureInfo.InvariantCulture)} bits");
            }

            // Show warning if there likely were not enough testcases
            const double warnThreshold = 0.9;
            double testcaseCountBits = Math.Log(_testcaseInstructionHashes.Count, 2);
            if(maximumMutualInformation > testcaseCountBits - warnThreshold)
                await Logger.LogWarningAsync(
                    "For some instructions the calculated mutual information is suspiciously near to the testcase range. It is recommended to run more testcases.\n");
        }

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract output path
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath == null)
                throw new ConfigurationException("Missing output directory for analysis results.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Utility class to hold a tuple of testcase count/hash count.
        /// </summary>
        private class InstructionData
        {
            public int TestcaseCount { get; set; }
            public Dictionary<byte[], int> HashCounts { get; }

            public InstructionData()
            {
                TestcaseCount = 0;
                HashCounts = new Dictionary<byte[], int>(new ByteArrayComparer());
            }
        }
    }
}