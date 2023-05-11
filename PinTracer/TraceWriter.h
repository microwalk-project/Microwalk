#pragma once
/*
Contains structs to store the trace data.
*/

// The size of the entry buffer.
#define ENTRY_BUFFER_SIZE 16384


/* INCLUDES */
#include "pin.H"
#include <iostream>
#include <fstream>
#include <sstream>


/* TYPES */

// The different types of trace entries.
enum struct TraceEntryTypes : UINT32
{
    // A memory read access.
    MemoryRead = 1,

    // A memory write access.
    MemoryWrite = 2,

    // The size parameter of a heap allocation ("malloc").
    HeapAllocSizeParameter = 3,

    // The return address of a heap allocation ("malloc").
    HeapAllocAddressReturn = 4,

    // The address parameter of a heap deallocation ("malloc").
    HeapFreeAddressParameter = 5,

    // A code branch.
    Branch = 6,

    // Stack pointer information.
    StackPointerInfo = 7,

    // A modification of the stack pointer.
    StackPointerModification = 8
};

// Represents one entry in a trace buffer.
#pragma pack(push, 1)
struct TraceEntry
{
    // The type of this entry.
    TraceEntryTypes Type;

    // Flag.
    // Used with: Branch, StackAllocation, StackDeallocation.
    UINT8 Flag;

    // (Padding for reliable parsing by analysis programs)
    UINT8 _padding1;

    // The size of a memory access.
    // Used with: MemoryRead, MemoryWrite
    UINT16 Param0;

    // The address of the instruction triggering the trace entry creation, or the size of an allocation.
    // Used with: MemoryRead, MemoryWrite, Branch, AllocSizeParameter, StackPointerInfo, StackPointerModification.
    UINT64 Param1;

    // The accessed/passed memory address.
    // Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch, StackPointerInfo, StackPointerModification.
    UINT64 Param2;
};
#pragma pack(pop)
static_assert(sizeof(TraceEntry) == 4 + 1 + 1 + 2 + 8 + 8, "Wrong size of TraceEntry struct");

// Flags for various trace entries.
enum struct TraceEntryFlags : UINT8
{
    // Branch taken: 1 Bit
    BranchNotTaken = 0 << 0,
    BranchTaken = 1 << 0,

    // Branch type: 2 Bits
    BranchTypeJump = 1 << 1,
    BranchTypeCall = 2 << 1,
    BranchTypeReturn = 3 << 1,

    // Stack (de)allocations
    StackIsCall = 1 << 0,
    StackIsReturn = 2 << 0,
    StackIsOther = 3 << 0
};

// Provides functions to write trace buffer contents into a log file.
// The prefix handling of this class is designed for single-threaded mode!
class TraceWriter
{
private:
    // The path prefix of the output file.
    std::string _outputFilenamePrefix;

    // The file where the trace data is currently written to.
	std::ofstream _outputFileStream;

    // The name of the currently open output file.
	std::string _currentOutputFilename;

    // The buffer entries.
    TraceEntry _entries[ENTRY_BUFFER_SIZE]{};

    // The current testcase ID.
    int _testcaseId = -1;

private:
    // Determines whether the program is currently tracing the trace prefix.
    static bool _prefixMode;
    
    // Determines whether the first return entry after testcase begin has been observed.
    static bool _sawFirstReturn;

    // The file where some additional trace prefix meta data is stored.
    static std::ofstream _prefixDataFileStream;

private:
    // Opens the output file and sets the respective internal state.
    void OpenOutputFile(std::string& filename);

public:

    // Creates a new trace logger.
    // -> filenamePrefix: The path prefix of the output file. Existing files are overwritten.
    explicit TraceWriter(const std::string& filenamePrefix);

    // Frees resources.
    ~TraceWriter();

    // Returns the address of the first buffer entry.
    TraceEntry* Begin();

    // Returns the address AFTER the last buffer entry.
    TraceEntry* End();

    // Writes the contents of the trace buffer into the output file.
    // -> end: A pointer to the address *after* the last entry to be written.
    void WriteBufferToFile(TraceEntry* end);

    // Sets the next testcase ID and opens a suitable trace file.
    void TestcaseStart(int testcaseId, TraceEntry* nextEntry);

    // Closes the current trace file and notifies the caller that the testcase has completed.
    void TestcaseEnd(TraceEntry* nextEntry);

public:

    // Checks whether the next entry points beyond the entry list, and flushes the entry list to the trace file in that case.
    // The function returns a pointer to the next entry.
    static TraceEntry* CheckBufferAndStore(TraceWriter *traceWriter, TraceEntry* nextEntry);

    // Creates a new MemoryRead entry.
    static TraceEntry* InsertMemoryReadEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress, UINT32 size);

    // Creates a new MemoryWrite entry.
    static TraceEntry* InsertMemoryWriteEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress, UINT32 size);

    // Creates a new HeapAllocSizeParameter entry.
    static TraceEntry* InsertHeapAllocSizeParameterEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, UINT64 size);
    static TraceEntry* InsertCallocSizeParameterEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, UINT64 count, UINT64 size);

    // Creates a new HeapAllocAddressReturn entry.
    static TraceEntry* InsertHeapAllocAddressReturnEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT memoryAddress);

    // Creates a new HeapFreeAddressParameter entry.
    static TraceEntry* InsertHeapFreeAddressParameterEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT memoryAddress);

    // Creates a new StackPointerModification entry.
    static TraceEntry* InsertStackPointerModificationEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT newStackPointer, UINT8 flags);

    // Creates a new Branch entry.
    static TraceEntry* InsertBranchEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT sourceAddress, ADDRINT targetAddress, UINT8 taken, UINT8 type);

    // Creates a new "ret" Branch entry.
    static TraceEntry* InsertRetBranchEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT sourceAddress, ADDRINT targetAddress);

    // Creates a new StackPointerInfo entry.
    static TraceEntry* InsertStackPointerInfoEntry(TraceWriter *traceWriter, TraceEntry* nextEntry, ADDRINT stackPointerMin, ADDRINT stackPointerMax);

    // Initializes the static part of the prefix mode (record image loads, even when the thread's TraceWriter object is not yet initialized).
    // -> filenamePrefix: The path prefix of the output file. Existing files are overwritten.
    static void InitPrefixMode(const std::string& filenamePrefix);

    // Writes information about the given loaded image into the trace metadata file.
    static void WriteImageLoadData(int interesting, uint64_t startAddress, uint64_t endAddress, std::string& name);
};

// Contains meta data of loaded images.
struct ImageData
{
public:
    bool _interesting;
	std::string _name;
    UINT64 _startAddress;
    UINT64 _endAddress;

public:
    // Constructor.
    ImageData(bool interesting, std::string name, UINT64 startAddress, UINT64 endAddress);

    // Checks whether the given basic block is contained in this image.
    [[nodiscard]] bool ContainsBasicBlock(BBL basicBlock) const;

    // Returns whether this image is considered interesting.
    [[nodiscard]] bool IsInteresting() const;
};