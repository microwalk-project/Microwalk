/* INCLUDES */
#include "TraceWriter.h"
#include "pin.H"
#include <iostream>
#include <fstream>
#include <sstream>


/* STATIC VARIABLES */

bool TraceWriter::_prefixMode;
ofstream TraceWriter::_prefixDataFileStream;


/* TYPES */

TraceWriter::TraceWriter(string filenamePrefix)
{
    // Remember prefix
    _outputFilenamePrefix = filenamePrefix;

    // Open prefix output file
    string filename = static_cast<ostringstream&>(ostringstream() << filenamePrefix << "prefix.trace").str();
    OpenOutputFile(filename);
}

TraceWriter::~TraceWriter()
{
    // Close file stream
    _outputFileStream.close();
}

void TraceWriter::InitPrefixMode(const string& filenamePrefix)
{
    // Start trace prefix mode
    _prefixMode = true;

    // Open prefix metadata output file
    _prefixDataFileStream.exceptions(ofstream::failbit | ofstream::badbit);
    string prefixDataFilename = static_cast<ostringstream&>(ostringstream() << filenamePrefix << "prefix_data.txt").str();
    _prefixDataFileStream.open(prefixDataFilename.c_str(), ofstream::out | ofstream::trunc);
    if(!_prefixDataFileStream)
    {
        cerr << "Error: Could not open prefix metadata output file '" << prefixDataFilename << "'." << endl;
        exit(1);
    }
    cerr << "Trace prefix mode started" << endl;
}

TraceEntry* TraceWriter::Begin()
{
    return _entries;
}

TraceEntry* TraceWriter::End()
{
    return &_entries[ENTRY_BUFFER_SIZE];
}

void TraceWriter::OpenOutputFile(string& filename)
{
    // Open file for writing
    _outputFileStream.exceptions(ofstream::failbit | ofstream::badbit);
    _currentOutputFilename = filename;
    _outputFileStream.open(_currentOutputFilename.c_str(), ofstream::out | ofstream::trunc | ofstream::binary);
    if(!_outputFileStream)
    {
        cerr << "Error: Could not open output file '" << _currentOutputFilename << "'." << endl;
        exit(1);
    }
}

void TraceWriter::WriteBufferToFile(TraceEntry* end)
{
    // Write buffer contents
    if(_testcaseId != -1 || _prefixMode)
        _outputFileStream.write(reinterpret_cast<char*>(_entries), reinterpret_cast<ADDRINT>(end) - reinterpret_cast<ADDRINT>(_entries));
}

void TraceWriter::TestcaseStart(int testcaseId, TraceEntry* nextEntry)
{
    // Exit prefix mode if necessary
    if(_prefixMode)
        TestcaseEnd(nextEntry);

    // Remember new testcase ID
    _testcaseId = testcaseId;

    // Open file for writing
    string filename = static_cast<ostringstream&>(ostringstream() << _outputFilenamePrefix << "t" << dec << _testcaseId << ".trace").str();
    OpenOutputFile(filename);
    cerr << "Switched to testcase #" << dec << _testcaseId << endl;
}

void TraceWriter::TestcaseEnd(TraceEntry* nextEntry)
{
    // Save remaining trace data
    if(nextEntry != _entries)
        WriteBufferToFile(nextEntry);

    // Close file handle and reset flags
    _outputFileStream.close();
    _outputFileStream.clear();

    // Exit prefix mode if necessary
    if(_prefixMode)
    {
        _prefixDataFileStream.close();
        _prefixMode = false;
        cerr << "Trace prefix mode ended" << endl;
    }
    else
    {
        // Notify caller that the trace file is complete
        cout << "t\t" << _currentOutputFilename << endl;
    }

    // Disable tracing until next test case starts
    _testcaseId = -1;
}

void TraceWriter::WriteImageLoadData(int interesting, uint64_t startAddress, uint64_t endAddress, string& name)
{
    // Prefix mode active?
    if(!_prefixMode)
    {
        cerr << "Image load ignored: " << name << endl;
        return;
    }

    // Write image data
    _prefixDataFileStream << "i\t" << interesting << "\t" << hex << startAddress << "\t" << hex << endAddress << "\t" << name << endl;
}

bool TraceWriter::CheckBufferFull(TraceEntry* nextEntry, TraceEntry* entryBufferEnd)
{
    return nextEntry != NULL && nextEntry == entryBufferEnd;
}

TraceEntry* TraceWriter::InsertMemoryReadEntry(TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress)
{
    // Create entry
    nextEntry->Type = TraceEntryTypes::MemoryRead;
    nextEntry->Param1 = instructionAddress;
    nextEntry->Param2 = memoryAddress;
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertMemoryWriteEntry(TraceEntry* nextEntry, ADDRINT instructionAddress, ADDRINT memoryAddress)
{
    // Create entry
    nextEntry->Type = TraceEntryTypes::MemoryWrite;
    nextEntry->Param1 = instructionAddress;
    nextEntry->Param2 = memoryAddress;
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertAllocSizeParameterEntry(TraceEntry* nextEntry, UINT64 size)
{
    // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
    if(nextEntry == NULL)
        return nextEntry;

    // Create entry
    nextEntry->Type = TraceEntryTypes::AllocSizeParameter;
    nextEntry->Param1 = size;
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertAllocAddressReturnEntry(TraceEntry* nextEntry, ADDRINT memoryAddress)
{
    // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
    if(nextEntry == NULL)
        return nextEntry;

    // Create entry
    nextEntry->Type = TraceEntryTypes::AllocAddressReturn;
    nextEntry->Param2 = memoryAddress;
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertFreeAddressParameterEntry(TraceEntry* nextEntry, ADDRINT memoryAddress)
{
    // Check whether given entry pointer is valid (we might be in a non-instrumented thread)
    if(nextEntry == NULL)
        return nextEntry;

    // Create entry
    nextEntry->Type = TraceEntryTypes::FreeAddressParameter;
    nextEntry->Param2 = memoryAddress;
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertBranchEntry(TraceEntry* nextEntry, ADDRINT sourceAddress, ADDRINT targetAddress, BOOL flag, UINT32 type)
{
    // Create entry
    nextEntry->Type = TraceEntryTypes::Branch;
    nextEntry->Param1 = sourceAddress;
    nextEntry->Param2 = targetAddress;
    nextEntry->Flag = static_cast<UINT8>((type << 1) | (flag == 0 ? 0 : 1));
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertRetBranchEntry(TraceEntry* nextEntry, ADDRINT sourceAddress, CONTEXT* contextAfterRet)
{
    // Create entry
    ADDRINT retAddress;
    PIN_GetContextRegval(contextAfterRet, REG_INST_PTR, reinterpret_cast<UINT8*>(&retAddress));
    return InsertBranchEntry(nextEntry, sourceAddress, retAddress, true, 2);
    return ++nextEntry;
}

TraceEntry* TraceWriter::InsertStackPointerWriteEntry(TraceEntry* nextEntry, ADDRINT stackPointerValue)
{
    // Create entry
    nextEntry->Type = TraceEntryTypes::StackPointerWrite;
    nextEntry->Param2 = stackPointerValue;
    return ++nextEntry;
}

ImageData::ImageData(bool interesting, string name, UINT64 startAddress, UINT64 endAddress)
{
    _interesting = interesting;
    _name = name;
    _startAddress = startAddress;
    _endAddress = endAddress;
}

bool ImageData::ContainsBasicBlock(BBL basicBlock)
{
    // Check start address
    return _startAddress <= INS_Address(BBL_InsHead(basicBlock)) && INS_Address(BBL_InsTail(basicBlock)) <= _endAddress;
}

bool ImageData::IsInteresting()
{
    return _interesting;
}