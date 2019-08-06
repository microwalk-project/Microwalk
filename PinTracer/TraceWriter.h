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

    // The size parameter of an allocation (typically malloc).
    AllocSizeParameter = 3,

    // The return address of an allocation (typically malloc).
    AllocAddressReturn = 4,

    // The address parameter of a deallocation (typically free).
    FreeAddressParameter = 5,

    // A code branch.
    Branch = 6,

    // A write to the stack pointer register (primarily add/sub and retn instructions, that impose large changes).
    StackPointerWrite = 7
};

// Represents one entry in a trace buffer.
#pragma pack(push, 1)
struct TraceEntry
{
    // The type of this entry.
    TraceEntryTypes Type;

    // Flag.
    // Used with: Branch.
    UINT8 Flag;

    // (Padding for reliable parsing by analysis programs)
    UINT8 _padding[3];

    // The address of the instruction triggering the trace entry creation, or the size of an allocation.
    // Used with: MemoryRead, MemoryWrite, Branch, AllocSizeParameter.
    UINT64 Param1;

    // The accessed/passed memory address.
    // Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch, StackPointerWrite.
    UINT64 Param2;
};
#pragma pack(pop)
static_assert(sizeof(TraceEntry) == 4 + 1 + 3 + 8 + 8, "Wrong size of TraceEntry struct");

// Provides functions to write trace buffer contents into a log file.
// The prefix handling of this class is designed for single-threaded mode!
class TraceWriter
{
private:
    // The path prefix of the output file.
    string _outputFilenamePrefix;

    // The file where the trace data is currently written to.
    ofstream _outputFileStream;

    // The name of the currently open output file.
    string _currentOutputFilename;

    // The buffer entries.
    TraceEntry _entries[ENTRY_BUFFER_SIZE];

    // The current testcase ID.
    int _testcaseId = -1;

private:
    // Determines whether the program is currently tracing the trace prefix.
    static bool _prefixMode;

    // The file where some additional trace prefix meta data is stored.
    static ofstream _prefixDataFileStream;

private:
    // Opens the output file and sets the respective internal state.
    void OpenOutputFile(string& filename);

public:

    // Creates a new trace logger.
    // -> filenamePrefix: The path prefix of the output file. Existing files are overwritten.
    TraceWriter(string filenamePrefix);

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

    // Determines whether the given buffer pointers are identical.
    static bool CheckBufferFull(TraceEntry* nextEntry, TraceEntry* entryBufferEnd);

    // Creates a new MemoryRead entry.
    static TraceEntry* InsertMemoryReadEntry(TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress);

    // Creates a new MemoryWrite entry.
    static TraceEntry* InsertMemoryWriteEntry(TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress);

    // Creates a new AllocSizeParameter entry.
    static TraceEntry* InsertAllocSizeParameterEntry(TraceEntry* nextEntry, UINT64 size);

    // Creates a new AllocAddressReturn entry.
    static TraceEntry* InsertAllocAddressReturnEntry(TraceEntry* nextEntry, ADDRINT memoryAddress);

    // Creates a new FreeAddressParameter entry.
    static TraceEntry* InsertFreeAddressParameterEntry(TraceEntry* nextEntry, ADDRINT memoryAddress);

    // Creates a new Branch entry.
    // type: 0 for jumps, 1 for call and 2 for ret.
    static TraceEntry* InsertBranchEntry(TraceEntry* nextEntry, ADDRINT sourceAddress, ADDRINT targetAddress, BOOL flag, UINT32 type);

    // Creates a new "ret" Branch entry.
    static TraceEntry* InsertRetBranchEntry(TraceEntry* nextEntry, ADDRINT sourceAddress, CONTEXT* contextAfterRet);

    // Creates a new StackPointerWrite entry.
    static TraceEntry* InsertStackPointerWriteEntry(TraceEntry* nextEntry, ADDRINT stackPointerValue);

    // Initializes the static part of the prefix mode (record image loads, even when the thread's TraceWriter object is not yet initialized).
    // -> filenamePrefix: The path prefix of the output file. Existing files are overwritten.
    static void InitPrefixMode(const string& filenamePrefix);

    // Writes information about the given loaded image into the trace metadata file.
    static void WriteImageLoadData(int interesting, uint64_t startAddress, uint64_t endAddress, string& name);
};

// Contains meta data of loaded images.
struct ImageData
{
public:
    bool _interesting;
    string _name;
    UINT64 _startAddress;
    UINT64 _endAddress;

public:
    // Constructor.
    ImageData(bool interesting, string name, UINT64 startAddress, UINT64 endAddress);

    // Checks whether the given basic block is contained in this image.
    bool ContainsBasicBlock(BBL basicBlock);

    // Returns whether this image is considered interesting.
    bool IsInteresting();
};