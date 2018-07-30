using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LeakageDetector
{
    /// <summary>
    /// Provides functionality to create a full diff of two trace files.
    /// </summary>
    public class TraceFileDiff
    {
        /// <summary>
        /// The first trace file being compared.
        /// </summary>
        public TraceFile TraceFile1 { get; }

        /// <summary>
        /// The second trace file being compared.
        /// </summary>
        public TraceFile TraceFile2 { get; }

        /// <summary>
        /// Creates a new comparer.
        /// </summary>
        /// <param name="traceFile1">The first trace file being compared.</param>
        /// <param name="traceFile2">The second trace file being compared.</param>
        public TraceFileDiff(TraceFile traceFile1, TraceFile traceFile2)
        {
            // Save parameters
            TraceFile1 = traceFile1;
            TraceFile2 = traceFile2;
        }

        /// <summary>
        /// Encodes all entries of the given trace file as 64-bit integers. This implies a loss of information.
        /// The least significant 4 bits of each entry will contain the entry type (directly casted from <see cref="TraceEntryTypes"/>).
        /// </summary>
        /// <param name="traceFile">The trace file to be encoded.</param>
        /// <returns>A list of encoded trace entries.</returns>
        public static IEnumerable<long> EncodeTraceEntries(TraceFile traceFile)
        {
            // Initialize stack memory allocation block
            AllocationData stackMemory = new AllocationData
            {
                AllocationLineNumber = 0,
                FreeLineNumber = traceFile.EntryCount, // Never freed
                StartAddress = traceFile.StackPointerMin,
                EndAddress = traceFile.StackPointerMax
            };

            // Run through entries
            int line = 0;
            SortedList<int, AllocationData> allocs = new SortedList<int, AllocationData>();
            foreach(TraceEntry entry in traceFile.Entries)
            {
                // Encode type right away
                ulong enc = (ulong)entry.EntryType;
                switch(entry.EntryType)
                {
                    case TraceEntryTypes.Allocation:
                    {
                        // Save allocation data
                        AllocationEntry allocationEntry = (AllocationEntry)entry;
                        allocs[line] = new AllocationData()
                        {
                            AllocationLineNumber = line,
                            FreeLineNumber = traceFile.EntryCount, // First assume as never freed, is overwritten as soon as a matching free is encountered
                            StartAddress = allocationEntry.Address,
                            EndAddress = allocationEntry.Address + allocationEntry.Size
                        };

                        // Encode allocation size
                        enc |= (ulong)allocationEntry.Size << 4;
                        break;
                    }

                    case TraceEntryTypes.Free:
                    {
                        // Find corresponding allocation block to mark as freed
                        FreeEntry freeEntry = (FreeEntry)entry;
                        var allocCandidates = allocs.Where(a => a.Key <= line && a.Value.FreeLineNumber >= line && a.Value.StartAddress == freeEntry.Address);
                        if(!allocCandidates.Any())
                        {
                            // TODO Error handling (same problem as in TraceFileComparer)
                            //Debug.WriteLine($"Warning! Processing line {line}: Skipped free() without any matching allocation block.");
                        }
                        else
                        {
                            // Mark block as freed
                            AllocationData allocData = allocCandidates.First().Value;
                            allocData.FreeLineNumber = line;
                        }

                        // Not encoded
                        break;
                    }

                    case TraceEntryTypes.ImageMemoryRead:
                    {
                        // Encode instruction and address offset
                        ImageMemoryReadEntry imageMemoryReadEntry = (ImageMemoryReadEntry)entry;
                        enc |= imageMemoryReadEntry.InstructionAddress << 4;
                        enc |= (ulong)imageMemoryReadEntry.MemoryAddress << 32;
                        break;
                    }

                    case TraceEntryTypes.ImageMemoryWrite:
                    {
                        // Encode instruction and address offset
                        ImageMemoryWriteEntry imageMemoryWriteEntry = (ImageMemoryWriteEntry)entry;
                        enc |= imageMemoryWriteEntry.InstructionAddress << 4;
                        enc |= (ulong)imageMemoryWriteEntry.MemoryAddress << 32;
                        break;
                    }

                    case TraceEntryTypes.AllocMemoryRead:
                    {
                        // Find related allocation block
                        // Stack or heap?
                        AllocMemoryReadEntry allocMemoryReadEntry = (AllocMemoryReadEntry)entry;
                        AllocationData allocData = stackMemory;
                        if(allocMemoryReadEntry.MemoryAddress < stackMemory.StartAddress || stackMemory.EndAddress < allocMemoryReadEntry.MemoryAddress)
                        {
                            // Heap?
                            var allocCandidates = allocs.Where(a => a.Key <= line && a.Value.FreeLineNumber >= line && a.Value.StartAddress <= allocMemoryReadEntry.MemoryAddress && allocMemoryReadEntry.MemoryAddress <= a.Value.EndAddress);
                            if(!allocCandidates.Any())
                            {
                                // TODO error handling - caused by missed malloc()
                                //Program.Log($"Error: Could not find allocation block for [{line}] {allocMemoryReadEntry.ToString()}\n", Program.LogLevel.Error);
                                Debug.WriteLine($"Error: Could not find allocation block for [{line}] {allocMemoryReadEntry.ToString()}");
                                break;
                            }
                            else
                                allocData = allocCandidates.Last().Value;
                        }

                        // Encode instruction address, the size of the allocation block, and the offset
                        enc |= allocMemoryReadEntry.InstructionAddress << 4;
                        enc |= (allocMemoryReadEntry.MemoryAddress - allocData.StartAddress) << 32;
                        //enc |= (allocData.EndAddress - allocData.StartAddress) << 48; // Only catches sizes less than 64 KB (16 bit address space)
                        break;
                    }

                    case TraceEntryTypes.AllocMemoryWrite:
                    {
                        // Find related allocation block
                        // Stack or heap?
                        AllocMemoryWriteEntry allocMemoryWriteEntry = (AllocMemoryWriteEntry)entry;
                        AllocationData allocData = stackMemory;
                        if(allocMemoryWriteEntry.MemoryAddress < stackMemory.StartAddress || stackMemory.EndAddress < allocMemoryWriteEntry.MemoryAddress)
                        {
                            // Heap?
                            var allocCandidates = allocs.Where(a => a.Key <= line && a.Value.FreeLineNumber >= line && a.Value.StartAddress <= allocMemoryWriteEntry.MemoryAddress && allocMemoryWriteEntry.MemoryAddress <= a.Value.EndAddress);
                            if(!allocCandidates.Any())
                            {
                                // TODO error handling - caused by missed malloc()
                                //Program.Log($"Error: Could not find allocation block for [{line}] {allocMemoryWriteEntry.ToString()}\n", Program.LogLevel.Error);
                                Debug.WriteLine($"Error: Could not find allocation block for [{line}] {allocMemoryWriteEntry.ToString()}");
                                break;
                            }
                            else
                                allocData = allocCandidates.Last().Value;
                        }

                        // Encode instruction address, the size of the allocation block, and the offset
                        enc |= allocMemoryWriteEntry.InstructionAddress << 4;
                        enc |= (allocMemoryWriteEntry.MemoryAddress - allocData.StartAddress) << 32;
                        //enc |= (allocData.EndAddress - allocData.StartAddress) << 48; // Only catches sizes less than 64 KB (16 bit address space)
                        break;
                    }

                    case TraceEntryTypes.Branch:
                    {
                        // Encode branch source and destination image offsets, and the "taken" flag
                        BranchEntry branchEntry = (BranchEntry)entry;
                        enc |= branchEntry.SourceInstructionAddress << 4;
                        enc |= (ulong)branchEntry.DestinationInstructionAddress << 32;
                        enc |= (ulong)(branchEntry.Taken ? 1 : 0) << 63;
                        break;
                    }
                }
                yield return unchecked((long)enc);
                ++line;
            }
        }

        /// <summary>
        /// Performs the diff.
        /// </summary>
        public IEnumerable<DiffItem> RunDiff()
        {
            // Get encoded version of trace files to simplify diff
            long[] encodedTraceEntries1 = EncodeTraceEntries(TraceFile1).ToArray();
            long[] encodedTraceEntries2 = EncodeTraceEntries(TraceFile2).ToArray();

            // Compare prefixes of trace files to reduce diff algorithm run time
            int remainingCommonLength = Math.Min(encodedTraceEntries1.Length, encodedTraceEntries2.Length);
            int prefixMatchLength = 0;
            for(; prefixMatchLength < remainingCommonLength; ++prefixMatchLength)
                if(encodedTraceEntries1[prefixMatchLength] != encodedTraceEntries2[prefixMatchLength])
                    break;
            yield return new DiffItem
            {
                Equal = true,
                StartLine1 = 0,
                EndLine1 = prefixMatchLength,
                StartLine2 = 0,
                EndLine2 = prefixMatchLength
            };

            // If there are any entries left, run diff tool
            if(encodedTraceEntries1.Length > prefixMatchLength)
            {
                var diff = Diff.DiffTools.DiffIntSequences(encodedTraceEntries1.Skip(prefixMatchLength).ToArray(), encodedTraceEntries2.Skip(prefixMatchLength).ToArray());
                int startIndex1 = 0;
                int startIndex2 = 0;
                foreach(var diffItem in diff)
                {
                    yield return new DiffItem
                    {
                        Equal = diffItem.Equal,
                        StartLine1 = prefixMatchLength + startIndex1,
                        EndLine1 = prefixMatchLength + diffItem.LastIndexA,
                        StartLine2 = prefixMatchLength + startIndex2,
                        EndLine2 = prefixMatchLength + diffItem.LastIndexB,
                    };
                    startIndex1 = diffItem.LastIndexA;
                    startIndex2 = diffItem.LastIndexB;
                }
            }
        }

        /// <summary>
        /// Contains metadata of one allocated memory block.
        /// </summary>
        private class AllocationData
        {
            /// <summary>
            /// The line number where the allocation was performed.
            /// </summary>
            public int AllocationLineNumber { get; set; }

            /// <summary>
            /// The line number where the allocated block was freed.
            /// </summary>
            public int FreeLineNumber { get; set; }

            /// <summary>
            /// The virtual start address of this memory block.
            /// </summary>
            public ulong StartAddress { get; set; }

            /// <summary>
            /// The virtual end address of this memory block.
            /// </summary>
            public ulong EndAddress { get; set; }
        }

        /// <summary>
        /// Contains data for one diff element.
        /// </summary>
        public class DiffItem
        {
            /// <summary>
            /// Tells whether the lines described by this diff element are equal or different.
            /// </summary>
            public bool Equal { get; set; }

            /// <summary>
            /// The starting line number in trace 1.
            /// </summary>
            public int StartLine1 { get; set; }

            /// <summary>
            /// The ending line number in trace 1 (exclusive).
            /// </summary>
            public int EndLine1 { get; set; }

            /// <summary>
            /// The starting line number in trace 2.
            /// </summary>
            public int StartLine2 { get; set; }

            /// <summary>
            /// The ending line number in trace 2 (exclusive).
            /// </summary>
            public int EndLine2 { get; set; }
        }
    }
}
