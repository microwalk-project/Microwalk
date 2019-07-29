using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Abstract base class for the entries of preprocessed trace files.
    /// </summary>
    public abstract class TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public abstract TraceEntryTypes EntryType { get; }
    }

    /// <summary>
    /// A memory allocation.
    /// </summary>
    public class AllocationEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.Allocation;

        /// <summary>
        /// The size of the allocated memory.
        /// </summary>
        public uint Size { get; set; }

        /// <summary>
        /// The address of the allocated memory.
        /// </summary>
        public ulong Address { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Alloc: {Address.ToString("X16")} :: {(Address + Size).ToString("X16")}  ({Size} bytes)";
    }

    /// <summary>
    /// A memory free.
    /// </summary>
    public class FreeEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.Free;

        /// <summary>
        /// The address of the freed memory.
        /// </summary>
        public ulong Address { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Free : {Address.ToString("X16")}";
    }

    /// <summary>
    /// An image memory read.
    /// </summary>
    public class ImageMemoryReadEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.ImageMemoryRead;

        /// <summary>
        /// The image ID of the reading instruction.
        /// </summary>
        public int InstructionImageId { get; set; }

        /// <summary>
        /// The name of the image of the reading instruction. Only for output purposes.
        /// </summary>
        public string InstructionImageName { get; set; }

        /// <summary>
        /// The address of the reading instruction, relative to the image start address.
        /// </summary>
        public uint InstructionAddress { get; set; }

        /// <summary>
        /// The image ID of the accessed memory.
        /// </summary>
        public int MemoryImageId { get; set; }

        /// <summary>
        /// The name of the image of the accessed memory. Only for output purposes.
        /// </summary>
        public string MemoryImageName { get; set; }

        /// <summary>
        /// The address of the accessed memory, relative to the image start address.
        /// </summary>
        public uint MemoryAddress { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Read : <{InstructionImageName}+{InstructionAddress.ToString("X8")}> {MemoryImageName}+{MemoryAddress.ToString("X8")}";
    }

    /// <summary>
    /// An image memory write.
    /// </summary>
    public class ImageMemoryWriteEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.ImageMemoryWrite;

        /// <summary>
        /// The image ID of the writing instruction.
        /// </summary>
        public int InstructionImageId { get; set; }

        /// <summary>
        /// The name of the image of the writing instruction. Only for output purposes.
        /// </summary>
        public string InstructionImageName { get; set; }

        /// <summary>
        /// The address of the writing instruction, relative to the image start address.
        /// </summary>
        public uint InstructionAddress { get; set; }

        /// <summary>
        /// The image ID of the accessed memory.
        /// </summary>
        public int MemoryImageId { get; set; }

        /// <summary>
        /// The name of the image of the accessed memory. Only for output purposes.
        /// </summary>
        public string MemoryImageName { get; set; }

        /// <summary>
        /// The address of the accessed memory, relative to the image start address.
        /// </summary>
        public uint MemoryAddress { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Write: <{InstructionImageName}+{InstructionAddress.ToString("X8")}> {MemoryImageName}+{MemoryAddress.ToString("X8")}";
    }

    /// <summary>
    /// A heap memory read.
    /// </summary>
    public class AllocMemoryReadEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.AllocMemoryRead;

        /// <summary>
        /// The image ID of the reading instruction.
        /// </summary>
        public int ImageId { get; set; }

        /// <summary>
        /// The name of the image of the reading instruction. Only for output purposes.
        /// </summary>
        public string ImageName { get; set; }

        /// <summary>
        /// The address of the reading instruction, relative to the image start address.
        /// </summary>
        public uint InstructionAddress { get; set; }

        /// <summary>
        /// The address of the accessed memory.
        /// </summary>
        public ulong MemoryAddress { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Read : <{ImageName}+{InstructionAddress.ToString("X8")}> {MemoryAddress.ToString("X16")}";
    }

    /// <summary>
    /// A heap memory write.
    /// </summary>
    public class AllocMemoryWriteEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.AllocMemoryWrite;

        /// <summary>
        /// The image ID of the writing instruction.
        /// </summary>
        public int ImageId { get; set; }

        /// <summary>
        /// The name of the image of the writing instruction. Only for output purposes.
        /// </summary>
        public string ImageName { get; set; }

        /// <summary>
        /// The address of the writing instruction, relative to the image start address.
        /// </summary>
        public uint InstructionAddress { get; set; }

        /// <summary>
        /// The address of the accessed memory.
        /// </summary>
        public ulong MemoryAddress { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"Write: <{ImageName}+{InstructionAddress.ToString("X8")}> {MemoryAddress.ToString("X16")}";
    }

    /// <summary>
    /// A branch.
    /// </summary>
    public class BranchEntry : TraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public override TraceEntryTypes EntryType => TraceEntryTypes.Branch;

        /// <summary>
        /// The image ID of the source instruction.
        /// </summary>
        public int SourceImageId { get; set; }

        /// <summary>
        /// The name of the image of the source instruction. Only for output purposes.
        /// </summary>
        public string SourceImageName { get; set; }

        /// <summary>
        /// The address of the source instruction, relative to the image start address.
        /// </summary>
        public uint SourceInstructionAddress { get; set; }

        /// <summary>
        /// The image ID of the destination instruction.
        /// </summary>
        public int DestinationImageId { get; set; }

        /// <summary>
        /// The name of the image of the destination instruction. Only for output purposes.
        /// </summary>
        public string DestinationImageName { get; set; }

        /// <summary>
        /// The address of the destination instruction, relative to the image start address.
        /// </summary>
        public uint DestinationInstructionAddress { get; set; }

        /// <summary>
        /// Tells whether the branch was taken.
        /// </summary>
        public bool Taken { get; set; }

        /// <summary>
        /// The type of this branch.
        /// </summary>
        public BranchTypes BranchType { get; set; }

        /// <summary>
        /// Converts this entry into a string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            // Format depending on branch type
            if(BranchType == BranchTypes.Jump)
                return $"Jump : <{SourceImageName}+{SourceInstructionAddress.ToString("X8")}> to <{DestinationImageName}+{DestinationInstructionAddress.ToString("X8")}> {(Taken ? "" : "not ")} taken";
            else if(BranchType == BranchTypes.Call)
                return $"Call : <{SourceImageName}+{SourceInstructionAddress.ToString("X8")}> to <{DestinationImageName}+{DestinationInstructionAddress.ToString("X8")}>";
            else if(BranchType == BranchTypes.Ret)
                return $"Ret  : <{SourceImageName}+{SourceInstructionAddress.ToString("X8")}> to <{DestinationImageName}+{DestinationInstructionAddress.ToString("X8")}>";
            else
                return $"ERROR Could not format branch entry: Unknown type";
        }
    }

    /// <summary>
    /// The different types of trace entries.
    /// </summary>
    public enum TraceEntryTypes : byte
    {
        /// <summary>
        /// A read in the image file memory (usually .data or .r[o]data sections).
        /// </summary>
        ImageMemoryRead = 1,

        /// <summary>
        /// A write in the image file memory (usually .data section).
        /// </summary>
        ImageMemoryWrite = 2,

        /// <summary>
        /// A read in memory allocated on the heap.
        /// </summary>
        AllocMemoryRead = 3,

        /// <summary>
        /// A write in memory allocated on the heap.
        /// </summary>
        AllocMemoryWrite = 4,

        /// <summary>
        /// A memory allocation.
        /// </summary>
        Allocation = 5,

        /// <summary>
        /// A memory free.
        /// </summary>
        Free = 6,

        /// <summary>
        /// A code branch.
        /// </summary>
        Branch = 7
    };

    /// <summary>
    /// The different branch types.
    /// </summary>
    public enum BranchTypes : byte
    {
        /// <summary>
        /// Conditional or unconditional jump instructions that do not affect the stack pointer.
        /// </summary>
        Jump = 0,

        /// <summary>
        /// Call instructions.
        /// </summary>
        Call = 1,

        /// <summary>
        /// Return instructions.
        /// </summary>
        Ret = 2
    }
}
