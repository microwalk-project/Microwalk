using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;
using Standart.Hash.xxHash;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("instruction-memory-access-trace-leakage", "Calculates several trace leakage measures for each memory accessing instruction.")]
    internal class InstructionMemoryAccessTraceLeakage : AnalysisStage
    {
        /// <summary>
        /// Maps testcase IDs to lists of instruction hashes (testcase ID => instruction ID => hash).
        /// </summary>
        private readonly ConcurrentDictionary<int, Dictionary<ulong, byte[]>> _testcaseInstructionHashes = new();

        /// <summary>
        /// Maps instruction addresses to formatted instructions.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, string> _formattedInstructions = new();

        /// <summary>
        /// The output directory for analysis results.
        /// </summary>
        private DirectoryInfo _outputDirectory = null!;

        /// <summary>
        /// Output format.
        /// </summary>
        private OutputFormat _outputFormat = OutputFormat.Csv;

        /// <summary>
        /// Controls whether the entire final state should be written to a dump file.
        /// </summary>
        private bool _dumpFullData = false;

        /// <summary>
        /// MAP file collection for resolving symbol names.
        /// </summary>
        private MapFileCollection _mapFileCollection = null!;

        public override bool SupportsParallelism => true;

        public override Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Input check
            if(traceEntity.PreprocessedTraceFile == null)
                throw new Exception("Preprocessed trace is null. Is the preprocessor stage missing?");
            
            // Allocate dictionary for mapping instruction addresses to memory access hashes
            var instructionHashes = new Dictionary<ulong, byte[]>();

            // Hash all memory access instructions
            foreach(var traceEntry in traceEntity.PreprocessedTraceFile)
            {
                // Extract instruction and memory address IDs (some kind of hash consisting of image ID and relative address)
                ulong instructionId;
                ulong memoryAddressId;
                switch(traceEntry.EntryType)
                {
                    case TraceEntryTypes.HeapMemoryAccess:
                    {
                        var heapMemoryAccess = (HeapMemoryAccess)traceEntry;
                        instructionId = ((ulong)heapMemoryAccess.InstructionImageId << 32) | heapMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)heapMemoryAccess.HeapAllocationBlockId << 32) | heapMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[heapMemoryAccess.InstructionImageId],
                                                  heapMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntryTypes.ImageMemoryAccess:
                    {
                        var imageMemoryAccess = (ImageMemoryAccess)traceEntry;
                        instructionId = ((ulong)imageMemoryAccess.InstructionImageId << 32) | imageMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)imageMemoryAccess.MemoryImageId << 32) | imageMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[imageMemoryAccess.InstructionImageId],
                                                  imageMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntryTypes.StackMemoryAccess:
                    {
                        var stackMemoryAccess = (StackMemoryAccess)traceEntry;
                        instructionId = ((ulong)stackMemoryAccess.InstructionImageId << 32) | stackMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = stackMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[stackMemoryAccess.InstructionImageId],
                                                  stackMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    default:
                        continue;
                }

                // Retrieve old hash
                if(!instructionHashes.TryGetValue(instructionId, out var hash))
                {
                    hash = new byte[16];
                    instructionHashes.Add(instructionId, hash);
                }

                // Update hash:
                // newHash = hash(oldHash || address)
                var hashLeft = hash.AsSpan(0);
                var hashRight = hash.AsSpan(8);
                BinaryPrimitives.WriteUInt64LittleEndian(hashRight, memoryAddressId);
                BinaryPrimitives.WriteUInt64LittleEndian(hashLeft, xxHash64.ComputeHash(hash, 16));
            }

            // Store instruction hashes
            _testcaseInstructionHashes.AddOrUpdate(traceEntity.Id, instructionHashes, (_, h) => h);

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

                    // Store testcase IDs only when a full data dump is requested, since this is quite expensive
                    if(_dumpFullData)
                    {
                        // Make sure testcase ID list exists
                        if(!instructionData.HashTestcases.ContainsKey(instruction.Value))
                            instructionData.HashTestcases.Add(instruction.Value, new List<int>());
                        instructionData.HashTestcases[instruction.Value].Add(testcase.Key);
                    }
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
                    // Also store the hash value which has the lowest guessing entropy value
                    double minConditionalGuessingEntropy = double.MaxValue;
                    byte[] minConditionalGuessingEntropyHash = Array.Empty<byte>();
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        double traceConditionalGuessingEntropy = (hashCount.Value + 1.0) / 2;
                        if(traceConditionalGuessingEntropy < minConditionalGuessingEntropy)
                        {
                            minConditionalGuessingEntropy = traceConditionalGuessingEntropy;
                            minConditionalGuessingEntropyHash = hashCount.Key;
                        }
                    }

                    leakageResult.MinConditionalGuessingEntropy = minConditionalGuessingEntropy;
                    leakageResult.MinConditionalGuessingEntropyHash = minConditionalGuessingEntropyHash;
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
                    await mutualInformationWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: " +
                                                                 $"{instructionData.Value.MutualInformation.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderByDescending(l => l.Value.MinEntropy).ThenBy(mi => mi.Key))
                    await minEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: " +
                                                          $"{instructionData.Value.MinEntropy.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.ConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await condGuessEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: " +
                                                                $"{instructionData.Value.ConditionalGuessingEntropy.ToString("N", numberFormat)} guesses");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.MinConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await minCondGuessEntropyWriter.WriteLineAsync($"Instruction {_formattedInstructions[instructionData.Key]}: " +
                                                                   $"{instructionData.Value.MinConditionalGuessingEntropy.ToString("N", numberFormat)} guesses " +
                                                                   $"[{string.Concat(instructionData.Value.MinConditionalGuessingEntropyHash.Select(b => b.ToString("X2")))}]");
            }
            else if(_outputFormat == OutputFormat.Csv)
            {
                // Write all measures into one CSV file
                await using var csvWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "memory-access-trace-leakage.csv")));

                // Header
                string listSeparator = ";"; // TextInfo.ListSeparator is unreliable
                await csvWriter.WriteLineAsync("Instruction" +
                                               listSeparator +
                                               "Mutual Information" +
                                               listSeparator +
                                               "Minimum Entropy" +
                                               listSeparator +
                                               "Conditional Guessing Entropy" +
                                               listSeparator +
                                               "Minimum Conditional Guessing Entropy" +
                                               listSeparator +
                                               "Minimum Conditional Guessing Entropy Hash");

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
                                                   leakageData.MinConditionalGuessingEntropy.ToString("N3") +
                                                   listSeparator +
                                                   string.Concat(leakageData.MinConditionalGuessingEntropyHash.Select(b => b.ToString("X2"))));
                }
            }

            // Write entire state into file?
            if(_dumpFullData)
            {
                // Put everything into one big text file
                await using var writer = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "trace-hash-dump.txt")));

                // Structure:
                // instruction1
                //    hash1: testcaseId1, testcaseId2, ...
                //    hash2: testcaseId3, ...
                // instruction2
                // ...
                foreach(var instruction in instructions)
                {
                    // Instruction name
                    await writer.WriteLineAsync(_formattedInstructions[instruction.Key]);

                    // Hashes
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        // Write hash and number of hits
                        await writer.WriteAsync($"  {string.Concat(hashCount.Key.Select(b => b.ToString("X2")))}: [{hashCount.Value}]");

                        // Write testcases yielding this hash
                        // Try to merge consecutive test case IDs: "1 3 4 5 7" -> "1 3-5 7"
                        int consecutiveStart = -1;
                        int consecutiveCurrent = -1;
                        const int consecutiveThreshold = 2;
                        foreach(var testcaseId in instruction.Value.HashTestcases[hashCount.Key])
                        {
                            if(consecutiveStart == -1)
                            {
                                // Initialize first sequence
                                consecutiveStart = testcaseId;
                                consecutiveCurrent = testcaseId;
                            }
                            else if(testcaseId == consecutiveCurrent + 1)
                            {
                                // We are in a sequence
                                consecutiveCurrent = testcaseId;
                            }
                            else
                            {
                                // We left the previous sequence
                                // Did it reach the threshold? -> write it in the appropriate format
                                if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                                    await writer.WriteAsync($" {consecutiveStart}-{consecutiveCurrent}");
                                else
                                {
                                    for(int t = consecutiveStart; t <= consecutiveCurrent; ++t)
                                        await writer.WriteAsync($" {t}");
                                }

                                // New sequence
                                consecutiveStart = testcaseId;
                                consecutiveCurrent = testcaseId;
                            }
                        }

                        // Write remaining test case IDs of last sequence
                        if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                            await writer.WriteAsync($" {consecutiveStart}-{consecutiveCurrent}");
                        else
                        {
                            for(int t = consecutiveStart; t <= consecutiveCurrent; ++t)
                                await writer.WriteAsync($" {t}");
                        }

                        // End line
                        await writer.WriteLineAsync();
                    }
                }
            }
        }

        protected override async Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Extract output path
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing output directory for analysis results.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Load MAP files
            _mapFileCollection = new MapFileCollection(Logger);
            var mapFilesNode = moduleOptions.GetChildNodeWithKey("map-files");
            if(mapFilesNode is YamlSequenceNode mapFileListNode)
                foreach(var mapFileNode in mapFileListNode.Children)
                    await _mapFileCollection.LoadMapFileAsync(mapFileNode.GetNodeString() ?? throw new ConfigurationException("Invalid node type in map file list."));
       
            // Check output format
            string outputFormat = moduleOptions.GetChildNodeWithKey("output-format")?.GetNodeString() ?? throw new ConfigurationException("Missing output format.");
            if(outputFormat != null && !Enum.TryParse(outputFormat, true, out _outputFormat))
                throw new ConfigurationException("Invalid output format.");

            // Dump internal data?
            _dumpFullData = moduleOptions.GetChildNodeWithKey("dump-full-data")?.GetNodeBoolean() ?? false;
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
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

            /// <summary>
            /// This is only filled and used when a data dump is requested.
            /// </summary>
            public Dictionary<byte[], List<int>> HashTestcases { get; }

            public InstructionData()
            {
                TestcaseCount = 0;
                HashCounts = new Dictionary<byte[], int>(new ByteArrayComparer());
                HashTestcases = new Dictionary<byte[], List<int>>(new ByteArrayComparer());
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
            public byte[] MinConditionalGuessingEntropyHash { get; set; } = null!;
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