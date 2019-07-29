using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Checks for differences in execution flow and memory access patterns of two execution traces.
    /// The comparison only runs till the first mismatch. To get a full diff of two traces, use <see cref="TraceFileDiff"/> instead.
    /// </summary>
    public class TraceFileComparer
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
        /// Determines whether the comparison results shall be printed to the log.
        /// </summary>
        public bool PrintResults { get; set; }

        /// <summary>
        /// The comparison result.
        /// Available after running the comparison.
        /// </summary>
        public ComparisonResults Result { get; private set; } = ComparisonResults.NotRun;

        /// <summary>
        /// The line causing the comparison error. Only used if the given error applies to a specific line, else -1.
        /// Available after running the comparison.
        /// </summary>
        public int ComparisonErrorLine { get; private set; } = -1;

        /// <summary>
        /// The items causing a comparison error.
        /// Available after running the comparison and encountering an error.
        /// </summary>
        public Tuple<TraceEntry, TraceEntry> ComparisonErrorItems { get; private set; } = null;

        /// <summary>
        /// The allocations encountered during parsing.
        /// </summary>
        private SortedList<ulong, AllocationData> _allocs = new SortedList<ulong, AllocationData>();

        /// <summary>
        /// Creates a new comparer.
        /// </summary>
        /// <param name="traceFile1">The first trace file being compared.</param>
        /// <param name="traceFile2">The second trace file being compared.</param>
        /// <param name="printResults">Determines whether the comparison results shall be printed to the log.</param>
        public TraceFileComparer(TraceFile traceFile1, TraceFile traceFile2, bool printResults = true)
        {
            // Save parameters
            TraceFile1 = traceFile1;
            TraceFile2 = traceFile2;
            PrintResults = printResults;
        }

        /// <summary>
        /// Executes the comparison and returns whether the two traces have identical memory access patterns.
        /// </summary>
        /// <returns></returns>
        public bool Compare()
        {
            // Run linearily through trace entries and compare them
            // If the traces have different length, use the shorter one as reference
            // This should not lead to wrong results: The shorter trace won't just randomly end, instead there has to be some kind of branch mismatch before
            // Keep track of currently allocated datablocks
            AllocationData stackMemory = new AllocationData() // StartAddress = Top of stack, EndAddress = Bottom of stack
            {
                StartAddress1 = TraceFile1.StackPointerMin,
                StartAddress2 = TraceFile2.StackPointerMin,
                EndAddress1 = TraceFile1.StackPointerMax,
                EndAddress2 = TraceFile2.StackPointerMax
            };
            int totalEntries = Math.Min(TraceFile1.EntryCount, TraceFile2.EntryCount);
            var traceFile1Enumerator = TraceFile1.Entries.GetEnumerator();
            var traceFile2Enumerator = TraceFile2.Entries.GetEnumerator();
            for(int i = 0; i < totalEntries; ++i)
            {
                // Retrieve entries
                traceFile1Enumerator.MoveNext();
                traceFile2Enumerator.MoveNext();
                TraceEntry e1 = traceFile1Enumerator.Current;
                TraceEntry e2 = traceFile2Enumerator.Current;

                // The entries should have the same type
                // If not, something must have gone very wrong: Differing branches are detected, so we should not run into differing instructions
                if(e1.EntryType != e2.EntryType)
                {
                    Result = ComparisonResults.ExecutionFlow_DifferentType;
                    ComparisonErrorLine = i;
                    ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                    if(PrintResults)
                        PrintComparisonResult($"Entries #{i} have different type (probably some bigger error occured?).", false,
                            $"Trace 1: {e1.ToString()}",
                            $"Trace 2: {e2.ToString()}");
                    return false;
                }

                // Act depending on entry types
                switch(e1.EntryType)
                {
                    // Possible leakages:
                    // - Different branch targets
                    // - In one execution the branch is taken, in the other one not
                    case TraceEntryTypes.Branch:
                    {
                        // Cast entries
                        BranchEntry branch1 = (BranchEntry)e1;
                        BranchEntry branch2 = (BranchEntry)e2;

                        // Compare targets
                        if(branch1.DestinationImageId != branch2.DestinationImageId || branch1.DestinationInstructionAddress != branch2.DestinationInstructionAddress)
                        {
                            Result = ComparisonResults.ExecutionFlow_DifferentBranchTarget;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Different branch target: <{branch1.DestinationImageName}+{branch1.DestinationInstructionAddress.ToString("X8")}> vs. <{branch2.DestinationImageName}+{branch2.DestinationInstructionAddress.ToString("X8")}>", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // Compare "taken"
                        if(branch1.Taken != branch2.Taken)
                        {
                            if(branch1.Taken)
                            {
                                Result = ComparisonResults.ExecutionFlow_BranchTakenIn1;
                                ComparisonErrorLine = i;
                                ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                                if(PrintResults)
                                    PrintComparisonResult($"Entries #{i}: Branch to <{branch1.DestinationImageName}+{branch1.DestinationInstructionAddress.ToString("X8")}> taken in trace 1, but not in trace 2.", false,
                                        $"Trace 1: {e1.ToString()}",
                                        $"Trace 2: {e2.ToString()}");
                            }
                            else
                            {
                                Result = ComparisonResults.ExecutionFlow_BranchTakenIn2;
                                ComparisonErrorLine = i;
                                ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                                if(PrintResults)
                                    PrintComparisonResult($"Entries #{i}: Branch to <{branch1.DestinationImageName}+{branch1.DestinationInstructionAddress.ToString("X8")}> not taken in trace 1, but in trace 2.", false,
                                        $"Trace 1: {e1.ToString()}",
                                        $"Trace 2: {e2.ToString()}");
                            }
                            return false;
                        }

                        // OK
                        break;
                    }

                    // Store allocation metadata
                    // Possible leakages:
                    // - The allocation size might differ
                    case TraceEntryTypes.Allocation:
                    {
                        // Cast entries
                        AllocationEntry alloc1 = (AllocationEntry)e1;
                        AllocationEntry alloc2 = (AllocationEntry)e2;

                        // Compare sizes
                        if(alloc1.Size != alloc2.Size)
                        {
                            Result = ComparisonResults.MemoryAccess_DifferentAllocationSize;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Allocation size differs: {alloc1.Size} vs. {alloc2.Size}", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // Save allocation data
                        _allocs[alloc1.Address] = new AllocationData()
                        {
                            StartAddress1 = alloc1.Address,
                            EndAddress1 = alloc1.Address + alloc1.Size,
                            StartAddress2 = alloc2.Address,
                            EndAddress2 = alloc2.Address + alloc2.Size
                        };

                        // OK
                        break;
                    }

                    // Remove allocation metadata from list
                    case TraceEntryTypes.Free:
                    {
                        // Cast entries
                        FreeEntry free1 = (FreeEntry)e1;
                        FreeEntry free2 = (FreeEntry)e2;

                        // Make sure the frees apply to the same datablock
                        if(!_allocs.TryGetValue(free1.Address, out AllocationData freeCandidate))
                        {
                            /*Result = ComparisonResults.MemoryAccess_FreedBlockNotFound;
                            ComparisonErrorLine = i;
                            PrintComparisonResult($"Entries #{i}: Cannot find freed allocation block for execution trace 1.", false,
                                $"Trace 1: {e1.ToString()}",
                                $"Trace 2: {e2.ToString()}");
                            // TODO This line is triggered in the Alloc/Free prefix, when one malloc() call was missed
                            return false;*/
                        }
                        else if(freeCandidate.StartAddress2 != free2.Address)
                        {
                            Result = ComparisonResults.MemoryAccess_FreedBlockNotMatching;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Freed allocation block of execution trace 1 does not match freed block of execution trace 2.", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }
                        else
                        {
                            // OK, remove allocation block
                            _allocs.Remove(free1.Address);
                        }
                        break;
                    }

                    // Possible leakages:
                    // -> Different access offset in image
                    case TraceEntryTypes.ImageMemoryRead:
                    {
                        // Cast entries
                        ImageMemoryReadEntry read1 = (ImageMemoryReadEntry)e1;
                        ImageMemoryReadEntry read2 = (ImageMemoryReadEntry)e2;

                        // Compare relative access offsets
                        if(read1.MemoryAddress != read2.MemoryAddress)
                        {
                            Result = ComparisonResults.MemoryAccess_DifferentImageMemoryReadOffset;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Memory read offsets in image differ: {read1.MemoryImageName}+{read1.MemoryAddress.ToString("X")} vs. {read2.MemoryImageName}+{read2.MemoryAddress.ToString("X")}", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // OK
                        break;
                    }

                    // Possible leakages:
                    // -> Different access offset in image
                    case TraceEntryTypes.ImageMemoryWrite:
                    {
                        // Cast entries
                        ImageMemoryWriteEntry write1 = (ImageMemoryWriteEntry)e1;
                        ImageMemoryWriteEntry write2 = (ImageMemoryWriteEntry)e2;

                        // Compare relative access offsets
                        if(write1.MemoryAddress != write2.MemoryAddress)
                        {
                            Result = ComparisonResults.MemoryAccess_DifferentImageMemoryWriteOffset;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Memory write offsets in image differ: {write1.MemoryImageName}+{write1.MemoryAddress.ToString("X")} vs. {write2.MemoryImageName}+{write2.MemoryAddress.ToString("X")}", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // OK
                        break;
                    }

                    // Possible leakages:
                    // -> Different access offset (after subtracting allocation base addresses)
                    case TraceEntryTypes.AllocMemoryRead:
                    {
                        // Cast entries
                        AllocMemoryReadEntry read1 = (AllocMemoryReadEntry)e1;
                        AllocMemoryReadEntry read2 = (AllocMemoryReadEntry)e2;

                        // Find related allocation block
                        AllocationData allocCandidate = FindAllocationBlock(read1.MemoryAddress, read2.MemoryAddress);
                        if(allocCandidate == null)
                        {
                            // Stack access?
                            if(stackMemory.StartAddress1 <= read1.MemoryAddress && read1.MemoryAddress <= stackMemory.EndAddress1
                            && stackMemory.StartAddress2 <= read2.MemoryAddress && read2.MemoryAddress <= stackMemory.EndAddress2)
                                allocCandidate = stackMemory;
                            else
                            {
                                // TODO remove warning, better include the fs/gs segment bounds in the trace
                                if(PrintResults)
                                    PrintComparisonResult($"Entries #{i}: Cannot find common accessed allocation block. Maybe there are segment registers involved?", true,
                                        $"Trace 1: {read1.ToString()}",
                                        $"Trace 2: {read2.ToString()}",
                                        $"Stack: {stackMemory.ToString()}");
                                break;
                            }
                        }

                        // Calculate and compare relative access offsets
                        ulong offset1 = read1.MemoryAddress - allocCandidate.StartAddress1;
                        ulong offset2 = read2.MemoryAddress - allocCandidate.StartAddress2;
                        if(offset1 != offset2)
                        {
                            Result = ComparisonResults.MemoryAccess_DifferentAllocMemoryReadOffset;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Memory read offsets in common allocation block differ: +{offset1.ToString("X8")} vs. +{offset2.ToString("X8")}", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // OK
                        break;
                    }

                    // Possible leakages:
                    // -> Different access offset (after subtracting allocation/stack pointer base addresses)
                    case TraceEntryTypes.AllocMemoryWrite:
                    {
                        // Cast entries
                        AllocMemoryWriteEntry write1 = (AllocMemoryWriteEntry)e1;
                        AllocMemoryWriteEntry write2 = (AllocMemoryWriteEntry)e2;

                        // Find related allocation block
                        AllocationData allocCandidate = FindAllocationBlock(write1.MemoryAddress, write2.MemoryAddress);
                        if(allocCandidate == null)
                        {
                            // Stack access?
                            if(stackMemory.StartAddress1 <= write1.MemoryAddress && write1.MemoryAddress <= stackMemory.EndAddress1
                            && stackMemory.StartAddress2 <= write2.MemoryAddress && write2.MemoryAddress <= stackMemory.EndAddress2)
                                allocCandidate = stackMemory;
                            else
                            {
                                // TODO remove warning, better include the fs/gs segment bounds in the trace
                                if(PrintResults)
                                    PrintComparisonResult($"Entries #{i}: Cannot find common accessed allocation block. Maybe there are segment registers involved?", true,
                                        $"Trace 1: {write1.ToString()}",
                                        $"Trace 2: {write2.ToString()}",
                                        $"Stack: {stackMemory.ToString()}");
                                break;
                            }
                        }

                        // Calculate and compare relative access offsets
                        ulong offset1 = write1.MemoryAddress - allocCandidate.StartAddress1;
                        ulong offset2 = write2.MemoryAddress - allocCandidate.StartAddress2;
                        if(offset1 != offset2)
                        {
                            Result = ComparisonResults.MemoryAccess_DifferentAllocMemoryWriteOffset;
                            ComparisonErrorLine = i;
                            ComparisonErrorItems = new Tuple<TraceEntry, TraceEntry>(e1, e2);
                            if(PrintResults)
                                PrintComparisonResult($"Entries #{i}: Memory write offsets in common allocation block differ: +{offset1.ToString("X8")} vs. +{offset2.ToString("X8")}", false,
                                    $"Trace 1: {e1.ToString()}",
                                    $"Trace 2: {e2.ToString()}");
                            return false;
                        }

                        // OK
                        break;
                    }
                }
            }

            // No differences found
            Result = ComparisonResults.Match;
            return true;
        }

        /// <summary>
        /// Returns the allocation block with the given addresses.
        /// </summary>
        /// <param name="address1">The trace 1 address to be searched for.</param>
        /// <param name="address2">The trace 2 address to be searched for.</param>
        private AllocationData FindAllocationBlock(ulong address1, ulong address2)
        {
            // Find by start address
            // The blocks are sorted by start address, so the first hit should be the right one - else the correct block does not exist
            // TODO In all tests this list stayed quite small, implement it in O(log n) anyway?
            // AllocationData candidate = _allocs.Last(a => a.Key <= address1).Value;
            AllocationData candidate = null;
            for(int i = _allocs.Count - 1; i >= 0; --i)
            {
                if(_allocs.Keys[i] <= address1)
                {
                    candidate = _allocs.Values[i];
                    if(address1 <= candidate.EndAddress1 && candidate.StartAddress2 <= address2 && address2 <= candidate.EndAddress2)
                        return candidate;
                    break;
                }
            }
            return null;
        }

        /// <summary>
        /// Shows the given comparison result in the program log.
        /// </summary>
        /// <param name="message">The message of the comparison result.</param>
        /// <param name="warning">Determines whether the message shall be treated as a warning (not as an error).</param>
        /// <param name="information">Useful information to further track down the cause of the error.</param>
        private void PrintComparisonResult(string message, bool warning, params string[] information)
        {
            // Show message
            Program.Log($"\n{message}\n", warning ? Program.LogLevel.Warning : Program.LogLevel.Error);

            // Print information below message
            foreach(string info in information)
                Program.Log($"    {info}\n", Program.LogLevel.Debug);
        }

        /// <summary>
        /// Contains metadata of one allocated memory block.
        /// </summary>
        private class AllocationData
        {
            /// <summary>
            /// The virtual start address of this memory block in the first execution.
            /// </summary>
            public ulong StartAddress1 { get; set; }

            /// <summary>
            /// The virtual end address of this memory block in the first execution.
            /// </summary>
            public ulong EndAddress1 { get; set; }

            /// <summary>
            /// The virtual start address of this memory block in the second execution.
            /// </summary>
            public ulong StartAddress2 { get; set; }

            /// <summary>
            /// The virtual end address of this memory block in the second execution.
            /// </summary>
            public ulong EndAddress2 { get; set; }

            /// <summary>
            /// Returns a string representation of this allocation data.
            /// </summary>
            /// <returns></returns>
            public override string ToString() => $"{StartAddress1.ToString("X16")} - {EndAddress1.ToString("X16")}    ||    {StartAddress2.ToString("X16")} - {EndAddress2.ToString("X16")}";
        }

        /// <summary>
        /// The possible comparison results. Used to divide 
        /// </summary>
        public enum ComparisonResults
        {
            /// <summary>
            /// Comparison hasn't run yet.
            /// </summary>
            NotRun,

            /// <summary>
            /// Comparison was successful and revealed a match.
            /// </summary>
            Match,

            /// <summary>
            /// Execution flow comparison: The compared entries have a different type.
            /// </summary>
            ExecutionFlow_DifferentType,

            /// <summary>
            /// Execution flow comparison: The compared entries are branches with different targets.
            /// </summary>
            ExecutionFlow_DifferentBranchTarget,

            /// <summary>
            /// Execution flow comparison: The compared entries are branches with the same target, but only taken in the first trace.
            /// </summary>
            ExecutionFlow_BranchTakenIn1,

            /// <summary>
            /// Execution flow comparison: The compared entries are branches with the same target, but only taken in the second trace.
            /// </summary>
            ExecutionFlow_BranchTakenIn2,

            /// <summary>
            /// Memory access comparison: The size of an allocation differs between the compared entries.
            /// </summary>
            MemoryAccess_DifferentAllocationSize,

            /// <summary>
            /// Memory access comparison: The freed allocation block for execution trace entry 1 was not found.
            /// </summary>
            MemoryAccess_FreedBlockNotFound,

            /// <summary>
            /// Memory access comparison: The freed allocation block for execution trace entry 1 does not match the block for entry 2.
            /// </summary>
            MemoryAccess_FreedBlockNotMatching,

            /// <summary>
            /// Memory access comparison: The image memory read offset differs between the compared entries.
            /// </summary>
            MemoryAccess_DifferentImageMemoryReadOffset,

            /// <summary>
            /// Memory access comparison: The image memory write offset differs between the compared entries.
            /// </summary>
            MemoryAccess_DifferentImageMemoryWriteOffset,

            /// <summary>
            /// Memory access comparison: The heap memory read offset differs between the compared entries.
            /// </summary>
            MemoryAccess_DifferentAllocMemoryReadOffset,

            /// <summary>
            /// Memory access comparison: The heap memory write offset differs between the compared entries.
            /// </summary>
            MemoryAccess_DifferentAllocMemoryWriteOffset,

            /// <summary>
            /// Memory access comparison: The stack pointer is changed by different offsets between the compared entries.
            /// </summary>
            MemoryAccess_DifferentStackPointerOffset
        }
    }
}
