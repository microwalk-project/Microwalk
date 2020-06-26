using System;
using System.Buffers.Binary;
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
    [FrameworkModule("memory-access-trace-leakage", "Calculates several trace leakage measures for each memory accessing instruction.")]
    internal class MemoryAccessTraceLeakage : AnalysisStage
    {
        /// <summary>
        /// Maps testcase IDs to lists of instruction hashes (testcase ID => instruction ID => hash).
        /// </summary>
        private readonly ConcurrentDictionary<int, Dictionary<ulong, byte[]>> _testcaseInstructionHashes = new ConcurrentDictionary<int, Dictionary<ulong, byte[]>>();

        /// <summary>
        /// Maps instruction addresses to formatted instructions.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, string> _formattedInstructions = new ConcurrentDictionary<ulong, string>();

        /// <summary>
        /// The output directory for analysis results.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Output format.
        /// </summary>
        private OutputFormat _outputFormat = OutputFormat.Csv;

        /// <summary>
        /// MAP file collection for resolving symbol names.
        /// </summary>
        private readonly MapFileCollection _mapFileCollection = new MapFileCollection();

        public override bool SupportsParallelism => true;

        public override Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Allocate dictionary for mapping instruction addresses to memory access hashes
            Dictionary<ulong, byte[]> instructionHashes = new Dictionary<ulong, byte[]>();

            // Hash all memory access instructions
            foreach(var traceEntry in traceEntity.PreprocessedTraceFile.Entries)
            {
                // Extract instruction and memory address IDs (some kind of hash consisting of image ID and relative address)
                ulong instructionId;
                ulong memoryAddressId;
                switch(traceEntry.EntryType)
                {
                    case TraceEntry.TraceEntryTypes.HeapMemoryAccess:
                    {
                        var heapMemoryAccess = (HeapMemoryAccess)traceEntry;
                        instructionId = ((ulong)heapMemoryAccess.InstructionImageId << 32) | heapMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)heapMemoryAccess.MemoryAllocationBlockId << 32) | heapMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                            traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[heapMemoryAccess.InstructionImageId],
                            heapMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntry.TraceEntryTypes.ImageMemoryAccess:
                    {
                        var imageMemoryAccess = (ImageMemoryAccess)traceEntry;
                        instructionId = ((ulong)imageMemoryAccess.InstructionImageId << 32) | imageMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)imageMemoryAccess.MemoryImageId << 32) | imageMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                            traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[imageMemoryAccess.InstructionImageId],
                            imageMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntry.TraceEntryTypes.StackMemoryAccess:
                    {
                        var stackMemoryAccess = (StackMemoryAccess)traceEntry;
                        instructionId = ((ulong)stackMemoryAccess.InstructionImageId << 32) | stackMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = stackMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                            traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[stackMemoryAccess.InstructionImageId],
                            stackMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    default:
                        continue;
                }

                // Update hash
                if(!instructionHashes.TryGetValue(instructionId, out var hash))
                {
                    hash = new byte[32];
                    instructionHashes.Add(instructionId, hash);
                }

                var hashSpan = hash.AsSpan();
                BinaryPrimitives.WriteUInt64LittleEndian(hashSpan, memoryAddressId); // Will overwrite first 8 bytes of hash
                Blake2s.ComputeAndWriteHash(hashSpan, hashSpan);
            }

            // Store instruction hashes
            _testcaseInstructionHashes.AddOrUpdate(traceEntity.Id, instructionHashes, (t, h) => h);

            // Done
            return Task.CompletedTask;
        }

        public override async Task FinishAsync()
        {
            var instructionLeakage = new Dictionary<ulong, InstructionLeakageResult>();

            // Transform instruction hash lists into better usable form
            await Logger.LogInfoAsync("Running memory access trace leakage analysis");
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

            // Calculate leakage measures for each instruction
            double maximumMutualInformation = 0.0;
            foreach(var instruction in instructions)
            {
                var leakageResult = new InstructionLeakageResult();
                instructionLeakage.Add(instruction.Key, leakageResult);

                // Mutual information
                {
                    // Calculate probabilities of keys, and keys with traces (if they caused a call of this instruction)
                    // Since the keys are distinct and randomly generated, we have a uniform distribution
                    double pX = 1.0 / instruction.Value.TestcaseCount; // p(x)
                    double pXy = 1.0 / instruction.Value.TestcaseCount; // p(x,y)

                    // Calculate mutual information
                    double mutualInformation = 0.0;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        double pY = (double)hashCount.Value / instruction.Value.TestcaseCount; // p(y)
                        mutualInformation += hashCount.Value * pXy * Math.Log2(pXy / (pX * pY));
                    }

                    leakageResult.MutualInformation = mutualInformation;
                }

                // Minimum entropy
                {
                    // Compute amount of unique traces
                    int uniqueTraceCount = instruction.Value.HashCounts.Count;
                    leakageResult.MinEntropy = Math.Log2(uniqueTraceCount);
                }

                // Conditional guessing entropy
                {
                    // Sum guessing entropy for each trace, weighting by its probability -> average value
                    double conditionalGuessingEntropy = 0.0;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        // Probability of trace
                        double pY = (double)hashCount.Value / instruction.Value.TestcaseCount; // p(y)

                        // Sum over all possible inputs
                        // Application of Gaussian sum formula, simplification due to test cases being distinct and uniformly distributed -> p(x) = 1/n
                        conditionalGuessingEntropy += pY * (hashCount.Value + 1.0) / 2;
                    }

                    leakageResult.ConditionalGuessingEntropy = conditionalGuessingEntropy;
                }

                // Minimum conditional guessing entropy
                {
                    // Find minimum guessing entropy of each trace, weighting is not needed here
                    double minConditionalGuessingEntropy = double.MaxValue;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        double traceConditionalGuessingEntropy = (hashCount.Value + 1.0) / 2;
                        if(traceConditionalGuessingEntropy < minConditionalGuessingEntropy)
                            minConditionalGuessingEntropy = traceConditionalGuessingEntropy;
                    }

                    leakageResult.MinConditionalGuessingEntropy = minConditionalGuessingEntropy;
                }
            }

            // Show warning if there likely were not enough testcases
            const double warnThreshold = 0.9;
            double testcaseCountBits = Math.Log2(_testcaseInstructionHashes.Count);
            if(maximumMutualInformation > testcaseCountBits - warnThreshold)
                await Logger.LogWarningAsync("For some instructions the calculated mutual information is suspiciously near to the testcase range. It is recommended to run more testcases.");

            // Store results
            await Logger.LogInfoAsync("Memory access trace leakage analysis completed, writing results");
            if(_outputFormat == OutputFormat.Txt)
            {
                // Store each measure in an own text file
                string basePath = _outputDirectory.FullName;
                await using var mutualInformationWriter = new StreamWriter(File.Create(Path.Combine(basePath, "mutual-information.txt")));
                await using var minEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "minimum-entropy.txt")));
                await using var condGuessEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "conditional-guessing-entropy.txt")));
                await using var minCondGuessEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "minimum-conditional-guessing-entropy.txt")));

                // Sort instructions by leakage in descending order
                var numberFormat = new NumberFormatInfo { NumberDecimalSeparator = ".", NumberDecimalDigits = 3 };
                foreach(var instructionData in instructionLeakage.OrderByDescending(l => l.Value.MutualInformation).ThenBy(mi => mi.Key))
                    await mutualInformationWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: {instructionData.Value.MutualInformation.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderByDescending(l => l.Value.MinEntropy).ThenBy(mi => mi.Key))
                    await minEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: {instructionData.Value.MinEntropy.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.ConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await condGuessEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: {instructionData.Value.ConditionalGuessingEntropy.ToString("N", numberFormat)} guesses");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.MinConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await minCondGuessEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: {instructionData.Value.MinConditionalGuessingEntropy.ToString("N", numberFormat)} guesses");
            }
            else if(_outputFormat == OutputFormat.Csv)
            {
                // Write all measures into one CSV file
                await using var csvWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "memory-access-trace-leakage.csv")));

                // Header
                string listSeparator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                await csvWriter.WriteLineAsync("Instruction" +
                                               listSeparator +
                                               "Mutual Information" +
                                               listSeparator +
                                               "Minimum Entropy" +
                                               listSeparator +
                                               "Conditional Guessing Entropy" +
                                               listSeparator +
                                               "Minimum Conditional Guessing Entropy");

                // Write leakage data
                foreach(var instructionData in instructionLeakage)
                {
                    var leakageData = instructionData.Value;
                    await csvWriter.WriteLineAsync(_formattedInstructions[instructionData.Key] +
                                                   listSeparator +
                                                   leakageData.MutualInformation.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.MinEntropy.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.ConditionalGuessingEntropy.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.MinConditionalGuessingEntropy.ToString("N3"));
                }
            }
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract output path
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath == null)
                throw new ConfigurationException("Missing output directory for analysis results.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Load MAP files
            var mapFilesNode = moduleOptions.GetChildNodeWithKey("map-files");
            if(mapFilesNode != null && mapFilesNode is YamlSequenceNode mapFileListNode)
                foreach(var mapFileNode in mapFileListNode.Children)
                    await _mapFileCollection.LoadMapFileAsync(mapFileNode.GetNodeString());

            // Check output format
            string outputFormat = moduleOptions.GetChildNodeWithKey("output-format")?.GetNodeString();
            if(outputFormat != null && !Enum.TryParse(outputFormat, true, out _outputFormat))
                throw new ConfigurationException("Invalid output format.");
        }


        private void StoreFormattedInstruction(ulong instructionKey, TracePrefixFile.ImageFileInfo imageFileInfo, uint instructionAddress)
        {
            // Instruction already known?
            if(_formattedInstructions.ContainsKey(instructionKey))
                return;

            // Store formatted instruction
            _formattedInstructions.TryAdd(instructionKey, _mapFileCollection.FormatAddress(imageFileInfo, instructionAddress));
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

        /// <summary>
        /// Stores leakage information for one instruction.
        /// </summary>
        private class InstructionLeakageResult
        {
            public double MutualInformation { get; set; }
            public double MinEntropy { get; set; }
            public double ConditionalGuessingEntropy { get; set; }
            public double MinConditionalGuessingEntropy { get; set; }
        }

        /// <summary>
        /// Output formats.
        /// </summary>
        private enum OutputFormat
        {
            /// <summary>
            /// Output analysis results in text form (one file per measure).
            /// </summary>
            Txt,

            /// <summary>
            /// Output analysis in single CSV file (one column per measure).
            /// </summary>
            Csv
        }
    }
}