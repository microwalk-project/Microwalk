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

    // The address of the instruction triggering the trace entry creation.
    // Used with: MemoryRead, MemoryWrite, Branch, StackPointerWrite.
    UINT64 InstructionAddress;

    // The accessed/passed memory address.
    // Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch, StackPointerWrite.
    UINT64 MemoryAddress;

    // The passed size.
    // Used with: AllocSizeParameter.
    UINT64 Size;
};
#pragma pack(pop)
static_assert(sizeof(TraceEntry) == 4 + 1 + 3 + 8 + 8 + 8, "Wrong size of TraceEntry struct");

// Provides functions to write trace buffer contents into a log file.
class TraceLogger
{
private:
    // The path prefix of the output file.
    string _outputFilenamePrefix;

    // The file where the trace data is currently written to.
    ofstream _outputFileStream;

    // The buffer entries.
    TraceEntry _entries[ENTRY_BUFFER_SIZE];

    // The current testcase ID.
    // Testcase #0 is the prefix.
    int _testcaseId = -1;

public:

    // Creates a new trace logger.
    // -> filename: The path prefix of the output file. Existing files are overwritten.
    TraceLogger(string filenamePrefix)
    {
        // Remember prefix
        _outputFilenamePrefix = filenamePrefix;

        // Set trace prefix mode
        TestcaseStart(0);
    }

    // Frees resources.
    ~TraceLogger()
    {
        // Close file stream
        _outputFileStream.close();
    }

    // Returns the address of the first buffer entry.
    TraceEntry *Begin()
    {
        return _entries;
    }

    // Returns the address AFTER the last buffer entry.
    TraceEntry *End()
    {
        return &_entries[ENTRY_BUFFER_SIZE];
    }

    // Writes the contents of the trace buffer into the output file.
    // -> end: A pointer to the address *after* the last entry to be written.
    void WriteBufferToFile(TraceEntry *end)
    {
        // Write buffer contents
        if(_testcaseId != -1)
            _outputFileStream.write(reinterpret_cast<char *>(_entries), reinterpret_cast<ADDRINT>(end) - reinterpret_cast<ADDRINT>(_entries));
        /*for(TraceEntry *entry = _entries; entry != end; ++entry)
        {
            // Write entry data depending on type
            switch(entry->Type)
            {
                case TraceEntryTypes::MemoryRead:
                {
                    _outputFileStream << "MemoryRead: <" << hex << entry->InstructionAddress << ">: " << hex << entry->MemoryAddress << endl;
                    break;
                }

                case TraceEntryTypes::MemoryWrite:
                {
                    _outputFileStream << "MemoryWrite: <" << hex << entry->InstructionAddress << ">: " << hex << entry->MemoryAddress << endl;
                    break;
                }

                case TraceEntryTypes::AllocSizeParameter:
                {
                    _outputFileStream << "AllocSizeParameter: " << dec << entry->Size << endl;
                    break;
                }

                case TraceEntryTypes::AllocAddressReturn:
                {
                    _outputFileStream << "AllocAddressReturn: " << hex << entry->MemoryAddress << endl;
                    break;
                }

                case TraceEntryTypes::FreeAddressParameter:
                {
                    _outputFileStream << "FreeAddressParameter: " << hex << entry->MemoryAddress << endl;
                    break;
                }

                case TraceEntryTypes::Branch:
                {
                    UINT8 branchType = entry->Flag >> 1;
                    if(branchType == 0)
                    {
                        _outputFileStream << "Jump: <" << hex << entry->InstructionAddress << ">: ";
                        if(entry->Flag & 1)
                            _outputFileStream << hex << entry->MemoryAddress;
                        else
                            _outputFileStream << "not taken";
                    }
                    else if(branchType == 1)
                        _outputFileStream << "Call: <" << hex << entry->InstructionAddress << ">: " << hex << entry->MemoryAddress;
                    else if(branchType == 2)
                        _outputFileStream << "Ret: <" << hex << entry->InstructionAddress << ">: " << hex << entry->MemoryAddress;
                    _outputFileStream << endl;
                    break;
                }

                case TraceEntryTypes::StackPointerWrite:
                {
                    _outputFileStream << "StackPointer: <" << hex << entry->InstructionAddress << ">: " << hex << entry->MemoryAddress << endl;
                    break;
                }
            }
        }*/
    }

    // Sets the next testcase ID and opens a suitable trace file.
    void TestcaseStart(int testcaseId)
    {
        // Exit prefix mode if necessary
        if(_testcaseId == 0)
        {
            TestcaseEnd(_entries);
            //cerr << "Prefix mode ended." << endl;
        }

        // Remember new testcase ID
        _testcaseId = testcaseId;

        // Open file for writing
        _outputFileStream.exceptions(ofstream::failbit | ofstream::badbit);
        string filename = static_cast<ostringstream &>(ostringstream() << _outputFilenamePrefix << "_" << dec << _testcaseId << ".trace").str();
        _outputFileStream.open(filename.c_str(), ofstream::out | ofstream::trunc /*| ofstream::binary*/);
        if(!_outputFileStream)
        {
            cerr << "Error: Could not open output file '" << filename << "'." << endl;
            exit(1);
        }
        //cerr << "Switched to testcase #" << dec << _testcaseId << endl;
    }

    // Closes the current trace file and notifies the caller that the testcase has completed.
    void TestcaseEnd(TraceEntry *nextEntry)
    {
        // Save remaining trace data
        if(nextEntry != _entries)
            WriteBufferToFile(nextEntry);

        // Close file handle and reset flags
        _outputFileStream.close();
        _outputFileStream.clear();

        // Notify caller that the trace file is complete
        cout << "t\t" << dec << _testcaseId << endl;

        // Disable tracing until next test case starts
        _testcaseId = -1;
    }

public:

    // Determines whether the given buffer pointers are identical.
    static bool CheckBufferFull(TraceEntry *nextEntry, TraceEntry *entryBufferEnd)
    {
        return nextEntry != NULL && nextEntry == entryBufferEnd;
    }

    // Creates a new MemoryRead entry.
    static TraceEntry* InsertMemoryReadEntry(TraceEntry *nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress)
    {
        // Create entry
        nextEntry->Type = TraceEntryTypes::MemoryRead;
        nextEntry->InstructionAddress = instructionAddress;
        nextEntry->MemoryAddress = memoryAddress;
        return ++nextEntry;
    }

    // Creates a new MemoryWrite entry.
    static TraceEntry* InsertMemoryWriteEntry(TraceEntry *nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress)
    {
        // Create entry
        nextEntry->Type = TraceEntryTypes::MemoryWrite;
        nextEntry->InstructionAddress = instructionAddress;
        nextEntry->MemoryAddress = memoryAddress;
        return ++nextEntry;
    }

    // Creates a new AllocSizeParameter entry.
    static TraceEntry* InsertAllocSizeParameterEntry(TraceEntry *nextEntry, UINT64 size)
    {
        // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
        if(nextEntry == NULL)
            return nextEntry;

        // Create entry
        nextEntry->Type = TraceEntryTypes::AllocSizeParameter;
        nextEntry->Size = size;
        return ++nextEntry;
    }

    // Creates a new AllocAddressReturn entry.
    static TraceEntry* InsertAllocAddressReturnEntry(TraceEntry *nextEntry, ADDRINT memoryAddress)
    {
        // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
        if(nextEntry == NULL)
            return nextEntry;

        // Create entry
        nextEntry->Type = TraceEntryTypes::AllocAddressReturn;
        nextEntry->MemoryAddress = memoryAddress;
        return ++nextEntry;
    }

    // Creates a new FreeAddressParameter entry.
    static TraceEntry* InsertFreeAddressParameterEntry(TraceEntry *nextEntry, ADDRINT memoryAddress)
    {
        // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
        if(nextEntry == NULL)
            return nextEntry;

        // Create entry
        nextEntry->Type = TraceEntryTypes::FreeAddressParameter;
        nextEntry->MemoryAddress = memoryAddress;
        return ++nextEntry;
    }

    // Creates a new Branch entry.
    // type: 0 for jumps, 1 for call and 2 for ret.
    static TraceEntry* InsertBranchEntry(TraceEntry *nextEntry, ADDRINT sourceAddress, ADDRINT targetAddress, BOOL flag, UINT32 type)
    {
        // Create entry
        nextEntry->Type = TraceEntryTypes::Branch;
        nextEntry->InstructionAddress = sourceAddress;
        nextEntry->MemoryAddress = targetAddress;
        nextEntry->Flag = static_cast<UINT8>((type << 1) | (flag == 0 ? 0 : 1));
        return ++nextEntry;
    }

    // Creates a new "ret" Branch entry.
    static TraceEntry* InsertRetBranchEntry(TraceEntry *nextEntry, ADDRINT sourceAddress, CONTEXT *contextAfterRet)
    {
        // Create entry
        ADDRINT retAddress;
        PIN_GetContextRegval(contextAfterRet, REG_INST_PTR, reinterpret_cast<UINT8 *>(&retAddress));
        return InsertBranchEntry(nextEntry, sourceAddress, retAddress, true, 2);
        return ++nextEntry;
    }

    // Creates a new StackPointerWrite entry.
    static TraceEntry* InsertStackPointerWriteEntry(TraceEntry *nextEntry, ADDRINT instructionAddress, ADDRINT stackPointerValue)
    {
        // Create entry
        nextEntry->Type = TraceEntryTypes::StackPointerWrite;
        nextEntry->InstructionAddress = instructionAddress;
        nextEntry->MemoryAddress = stackPointerValue;
        return ++nextEntry;
    }
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
    ImageData(bool interesting, string name, UINT64 startAddress, UINT64 endAddress)
    {
        _interesting = interesting;
        _name = name;
        _startAddress = startAddress;
        _endAddress = endAddress;
    }

    // Checks whether the given basic block is contained in this image.
    bool ContainsBasicBlock(BBL basicBlock)
    {
        // Check start address
        return _startAddress <= INS_Address(BBL_InsHead(basicBlock)) && INS_Address(BBL_InsTail(basicBlock)) <= _endAddress;
    }

    // Returns whether this image is considered interesting.
    bool IsInteresting()
    {
        return _interesting;
    }
};