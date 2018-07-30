/*
IMPORTANT: The instrumented program or one of its dependencies MUST contain (named) "malloc" and "free" functions.
To get meaningful outputs, make sure that these functions are called with "call" and have a "ret" instruction (no "jmp" to another function).
*/


/* INCLUDES */
#include "Storage.h"
#include <xed-interface.h>
#include "AuxiliaryFunctions.h"
#include "CpuFeatureDefinitions.h"


/* GLOBAL VARIABLES */

// The output file command line option.
KNOB<string> KnobOutputFilePrefix(KNOB_MODE_WRITEONCE, "pintool", "o", "out", "specify file name/path prefix for LeakageDetectorTrace output");

// The names of interesting images, separated by semicolons.
KNOB<string> KnobInterestingImageList(KNOB_MODE_WRITEONCE, "pintool", "i", ".exe", "specify list of interesting images, separated by semicolons");

// The desired CPU feature level.
KNOB<int> KnobCpuFeatureLevel(KNOB_MODE_WRITEONCE, "pintool", "c", "0", "specify desired CPU model: 0 = Default, 1 = Pentium3, 2 = Merom, 3 = Westmere, 4 = Ivybridge (your own CPU should form a superset of the selected option)");

// Constant random number generator value.
// Magic default value is 0xBADBADBADBADBAD (Pin does not provide an API to check whether parameter is actually in the command line).
KNOB<unsigned long long> KnobFixedRandomNumbers(KNOB_MODE_WRITEONCE, "pintool", "r", "841534158063459245", "set constant output for RDRAND instruction");

// Determines whether the CPU feature level is changed at all.
bool _emulateCpuModel = true;

// The emulated CPU.
static cpuid_model_t *_emulatedCpuModelInfo = nullptr;

// The names of interesting images, parsed from the command line option.
vector<string> _interestingImages;

// The thread local storage key for the trace logger objects.
TLS_KEY _traceLoggerTlsKey;

// The next writable entry buffer position (per thread).
REG _nextBufferEntryReg;

// The end of the entry buffer (per thread).
REG _entryBufferEndReg;

// The EAX input register of a CPUID instruction.
REG _cpuIdEaxInputReg;

// The ECX input register of a CPUID instruction.
REG _cpuIdEcxInputReg;

// Data of loaded images for lookup during trace instrumentation.
vector<ImageData *> _images;

// Determines whether RDRAND random numbers shall be replaced by fixed ones.
bool _useFixedRandomNumber = false;

// The fixed random number to be returned after each RDRAND instruction.
unsigned long long _fixedRandomNumber = 0;

/* CALLBACK PROTOTYPES */

VOID InstrumentTrace(TRACE trace, VOID *v);
VOID ThreadStart(THREADID tid, CONTEXT *ctxt, INT32 flags, VOID *v);
VOID ThreadFini(THREADID tid, const CONTEXT *ctxt, INT32 code, VOID *v);
VOID InstrumentImage(IMG img, VOID *v);
TraceEntry *CheckBufferAndStore(TraceEntry *nextEntry, TraceEntry *entryBufferEnd, THREADID tid);
TraceEntry *TestcaseStart(ADDRINT newTestcaseId, THREADID tid);
TraceEntry *TestcaseEnd(TraceEntry *nextEntry, THREADID tid);
EXCEPT_HANDLING_RESULT HandlePinToolException(THREADID tid, EXCEPTION_INFO *exceptionInfo, PHYSICAL_CONTEXT *physicalContext, VOID *v);
ADDRINT CheckNextTraceEntryPointerValid(TraceEntry *nextEntry);
void ChangeRandomNumber(ADDRINT *outputReg);
void ChangeCpuId(UINT32 inputEax, UINT32 inputEcx, UINT32 *outputEax, UINT32 *outputEbx, UINT32 *outputEcx, UINT32 *outputEdx);


/* FUNCTIONS */

// The main procedure of the tool.
int main(int argc, char *argv[])
{
    // Initialize PIN library. Print help message if -h(elp) is specified in the command line or the command line is invalid 
    if(PIN_Init(argc, argv))
    {
        cerr << "TODO useful output regarding the LeakageDetectorTrace tool." << endl << endl;
        cerr << KNOB_BASE::StringKnobSummary() << endl;
        return -1;
    }

    // Split list of interesting images
    stringstream interestingImagesStringStream(KnobInterestingImageList);
    string item;
    while(getline(interestingImagesStringStream, item, ';'))
        if(!item.empty())
        {
            tolower(item);
            _interestingImages.push_back(item);
        }

    // Create trace entry buffer and all associated variables
    _traceLoggerTlsKey = PIN_CreateThreadDataKey(0);
    _nextBufferEntryReg = PIN_ClaimToolRegister();
    _entryBufferEndReg = PIN_ClaimToolRegister();

    // Reserve tool registers for CPUID modification
    _cpuIdEaxInputReg = PIN_ClaimToolRegister();
    _cpuIdEcxInputReg = PIN_ClaimToolRegister();

    // Check desired model for CPU emulation
    switch(KnobCpuFeatureLevel.Value())
    {
        case 1:
            _emulatedCpuModelInfo = &model_Pentium3;
            break;
        case 2:
            _emulatedCpuModelInfo = &model_Merom;
            break;
        case 3:
            _emulatedCpuModelInfo = &model_Westmere;
            break;
        case 4:
            _emulatedCpuModelInfo = &model_Ivybridge;
            break;
        default:
            _emulateCpuModel = false;
            break;
    }

    // Check if constant random numbers are desired
    if(KnobFixedRandomNumbers.Value() != 0xBADBADBADBADBAD)
    {
        _useFixedRandomNumber = true;
        _fixedRandomNumber = KnobFixedRandomNumbers.Value();
        cerr << "Using fixed RDRAND output " << _fixedRandomNumber << endl;
    }

    // Instrument instructions and routines
    IMG_AddInstrumentFunction(InstrumentImage, 0);
    TRACE_AddInstrumentFunction(InstrumentTrace, 0);

    // Set thread event handlers
    PIN_AddThreadStartFunction(ThreadStart, 0);
    PIN_AddThreadFiniFunction(ThreadFini, 0);

    // Handle internal exceptions (for debugging)
    PIN_AddInternalExceptionHandler(HandlePinToolException, NULL);

    //cerr << "===============================================" << endl;
    //cerr << "This application is instrumented by LeakageDetectorTrace" << endl;
    //cerr << "See files '" << KnobOutputFilePrefix.Value() << "_*.trace' for analysis results" << endl;
    //cerr << "===============================================" << endl;

    // Load symbols to access function name information
    PIN_InitSymbols();

    // Start the program
    PIN_StartProgram();
    return 0;
}


/* CALLBACKS */

// [Callback] Instruments memory access instructions.
VOID InstrumentTrace(TRACE trace, VOID *v)
{
    // Check each instruction in each basic block
    for(BBL bbl = TRACE_BblHead(trace); BBL_Valid(bbl); bbl = BBL_Next(bbl))
    {
        // Before instrumentation check first whether we are in an interesting image
        // TODO this skips branches from uninteresting images to interesting images -> relevant?
        ImageData *img = nullptr;
        for(ImageData *i : _images)
            if(i->ContainsBasicBlock(bbl))
            {
                img = i;
                break;
            }
        if(img == nullptr)
        {
            // Should not happen
            cerr << "Error: Basic block " << hex << BBL_Address(bbl) << " in unknown image instrumented" << endl;
            continue;
        }
        bool interesting = img->IsInteresting();

        // Always save current stack pointer at beginning of block
        BBL_InsertIfCall(bbl, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_END);
        BBL_InsertThenCall(bbl, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertStackPointerWriteEntry),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_INST_PTR,
            IARG_REG_VALUE, REG_STACK_PTR,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);
        BBL_InsertIfCall(bbl, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, _entryBufferEndReg,
            IARG_END);
        BBL_InsertThenCall(bbl, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, _entryBufferEndReg,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        // Run through instructions
        for(INS ins = BBL_InsHead(bbl); INS_Valid(ins); ins = INS_Next(ins))
        {
            // Ignore everything that uses segment registers (shouldn't be used by modern software, except in a few cases by operating systems)
            // Windows e.g. uses GS for thread local storage
            // TODO Hint that in documentation
            // TODO LeakageDetector still throws a warning => Output segment data to create suitable allocation block
            if(INS_SegmentPrefix(ins))
                continue;

            // Ignore frequent push/pop stack instructions to reduce overhead (this might require a bit of interpolation at analysis time)
            OPCODE opc = INS_Opcode(ins);
            if(opc >= XED_ICLASS_PUSH && opc <= XED_ICLASS_PUSHFQ)
                continue;
            if(opc >= XED_ICLASS_POP && opc <= XED_ICLASS_POPFQ)
                continue;

            // Change CPUID instruction
            if(opc == XED_ICLASS_CPUID && _emulateCpuModel)
            {
                // Save input registers
                INS_InsertCall(ins, IPOINT_BEFORE, AFUNPTR(PIN_SetContextReg),
                    IARG_CONTEXT,
                    IARG_UINT32, _cpuIdEaxInputReg,
                    IARG_REG_VALUE, REG_EAX,
                    IARG_END);
                INS_InsertCall(ins, IPOINT_BEFORE, AFUNPTR(PIN_SetContextReg),
                    IARG_CONTEXT,
                    IARG_UINT32, _cpuIdEcxInputReg,
                    IARG_REG_VALUE, REG_ECX,
                    IARG_END);

                // Modify output registers
                INS_InsertCall(ins, IPOINT_AFTER, AFUNPTR(ChangeCpuId),
                    IARG_REG_VALUE, _cpuIdEaxInputReg,
                    IARG_REG_VALUE, _cpuIdEcxInputReg,
                    IARG_REG_REFERENCE, REG_EAX,
                    IARG_REG_REFERENCE, REG_EBX,
                    IARG_REG_REFERENCE, REG_ECX,
                    IARG_REG_REFERENCE, REG_EDX,
                    IARG_END);
                continue;
            }

            // Change RDRAND instruction
            if(opc == XED_ICLASS_RDRAND && _useFixedRandomNumber)
            {
                // Modify output register
                INS_InsertCall(ins, IPOINT_AFTER, AFUNPTR(ChangeRandomNumber),
                    IARG_REG_REFERENCE, INS_RegW(ins, 0),
                    IARG_END);
                continue;
            }

            // Trace branch instructions (conditional and unconditional)
            if(INS_IsCall(ins))
            {
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertBranchEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_BRANCH_TARGET_ADDR,
                    IARG_BOOL, 1,
                    IARG_UINT32, 1,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                continue;
            }
            if(INS_IsBranch(ins))
            {
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertBranchEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_BRANCH_TARGET_ADDR,
                    IARG_BRANCH_TAKEN,
                    IARG_UINT32, 0,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                continue;
            }
            if(INS_IsRet(ins))
            {
                // ret instructions cannot be instrumented with IPOINT_AFTER, since they do have no fallthrough
                INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceLogger::InsertRetBranchEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_CONTEXT,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
            }

            // We are only interested in stack operations like:
            //    add rsp, X
            //    sub rsp, X
            //    retn X
            // Other stack ops like call and ret (without parameter) are ignored, since they do little modification and just bloat the trace file
            xed_iform_enum_t insForm = xed_decoded_inst_get_iform_enum(INS_XedDec(ins));
            if(INS_RegWContain(ins, REG_STACK_PTR) && !INS_IsCall(ins) && insForm != XED_IFORM_RET_FAR && insForm != XED_IFORM_RET_NEAR)
            {
                LEVEL_VM::IPOINT pos = (INS_HasFallThrough(ins) ? IPOINT_AFTER : IPOINT_TAKEN_BRANCH);
                INS_InsertIfCall(ins, pos, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, pos, AFUNPTR(TraceLogger::InsertStackPointerWriteEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_REG_VALUE, REG_STACK_PTR,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, pos, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, pos, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                continue;
            }

            // Ignore everything else in uninteresting images
            if(!interesting)
                continue;

            // RET statements are caught as memory accesses, since they pop from the stack; we are not interested in them
            if(INS_IsRet(ins))
                continue;

            // Trace instructions with memory read
            if(INS_IsMemoryRead(ins) && INS_IsStandardMemop(ins))
            {
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertMemoryReadEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_MEMORYREAD_EA,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
            }


            // Trace instructions with a second memory read operand
            if(INS_HasMemoryRead2(ins) && INS_IsStandardMemop(ins))
            {
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertMemoryReadEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_MEMORYREAD2_EA,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
            }

            // Trace instructions with memory write
            if(INS_IsMemoryWrite(ins) && INS_IsStandardMemop(ins))
            {
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertMemoryWriteEntry),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_INST_PTR,
                    IARG_MEMORYWRITE_EA,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
                INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceLogger::CheckBufferFull),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_END);
                INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
                    IARG_REG_VALUE, _nextBufferEntryReg,
                    IARG_REG_VALUE, _entryBufferEndReg,
                    IARG_THREAD_ID,
                    IARG_RETURN_REGS, _nextBufferEntryReg,
                    IARG_END);
            }
        }
    }
}

// [Callback] Creates a new trace logger for the given new thread.
VOID ThreadStart(THREADID tid, CONTEXT *ctxt, INT32 flags, VOID *v)
{
    // Only instrument main thread
    // TODO add this in documentation
    if(tid == 0)
    {
        // Create new trace logger for this thread
        TraceLogger *traceLogger = new TraceLogger(KnobOutputFilePrefix.Value() + "_t" + decstr(tid));

        // Put logger into local storage of this thread
        PIN_SetThreadData(_traceLoggerTlsKey, traceLogger, tid);

        // Initialize entry buffer pointers
        PIN_SetContextReg(ctxt, _nextBufferEntryReg, reinterpret_cast<ADDRINT>(traceLogger->Begin()));
        PIN_SetContextReg(ctxt, _entryBufferEndReg, reinterpret_cast<ADDRINT>(traceLogger->End()));
    }
    else
    {
        // Set entry buffer pointers as null pointers
        PIN_SetContextReg(ctxt, _nextBufferEntryReg, NULL);
        PIN_SetContextReg(ctxt, _entryBufferEndReg, NULL);
    }
}

// [Callback] Cleans up after thread exit.
VOID ThreadFini(THREADID tid, const CONTEXT *ctxt, INT32 code, VOID *v)
{
    // Only the main thread is instrumented
    if(tid != 0)
        return;

    // Finalize trace logger of this thread
    TraceLogger *traceLogger = static_cast<TraceLogger *>(PIN_GetThreadData(_traceLoggerTlsKey, tid));
    traceLogger->WriteBufferToFile(reinterpret_cast<TraceEntry *>(PIN_GetContextReg(ctxt, _nextBufferEntryReg)));
    delete traceLogger;
    PIN_SetThreadData(_traceLoggerTlsKey, nullptr, tid);
}

// [Callback] Instruments the memory allocation/deallocation functions.
// TODO instrument malloc() and free() non Windows-specific
VOID InstrumentImage(IMG img, VOID *v)
{
    // Retrieve image name
    string imageName = IMG_Name(img);

    // Check whether image is interesting (its name appears in the image name list passed over the command line)
    string imageNameLower = imageName;
    tolower(imageNameLower);
    INT8 interesting = (find_if(_interestingImages.begin(), _interestingImages.end(), [&](string &interestingImageName) { return imageNameLower.find(interestingImageName) != string::npos; }) != _interestingImages.end()) ? 1 : 0;

    // Retrieve image memory offsets
    UINT64 imageStart = IMG_LowAddress(img);
    UINT64 imageEnd = IMG_HighAddress(img);

    // Notify caller
    cout << "i\t" << static_cast<int>(interesting) << "\t" << hex << imageStart << "\t" << hex << imageEnd << "\t" << imageName << endl;

    // Remember image for filtered trace instrumentation
    _images.push_back(new ImageData(interesting, imageName, imageStart, imageEnd));
    cerr << "Image '" << imageName << "' loaded at " << hex << imageStart << " ... " << hex << imageEnd << (interesting != 0 ? " [interesting]" : "") << endl;

    // Find the Pin notification functions to insert testcase markers
    RTN notifyStartRtn = RTN_FindByName(img, "PinNotifyTestcaseStart");
    if(RTN_Valid(notifyStartRtn))
    {
        RTN_Open(notifyStartRtn);

        // Switch to next testcase
        RTN_InsertCall(notifyStartRtn, IPOINT_BEFORE, AFUNPTR(TestcaseStart),
            IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        RTN_Close(notifyStartRtn);

        // Show debug info
        cerr << "    PinNotifyTestcaseStart() instrumented." << endl;
    }
    RTN notifyEndRtn = RTN_FindByName(img, "PinNotifyTestcaseEnd");
    if(RTN_Valid(notifyEndRtn))
    {
        RTN_Open(notifyEndRtn);

        // Close testcase
        RTN_InsertCall(notifyEndRtn, IPOINT_BEFORE, AFUNPTR(TestcaseEnd),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        RTN_Close(notifyEndRtn);

        // Show debug info
        cerr << "    PinNotifyTestcaseEnd() instrumented." << endl;
    }

    // Find the malloc() function to log allocation sizes and addresses    
    RTN mallocRtn = RTN_FindByName(img, "RtlAllocateHeap");
    if(RTN_Valid(mallocRtn))
    {
        RTN_Open(mallocRtn);

        // Trace size parameter
        RTN_InsertCall(mallocRtn, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertAllocSizeParameterEntry),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_FUNCARG_ENTRYPOINT_VALUE, 2,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);
        RTN_InsertCall(mallocRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, _entryBufferEndReg,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        // Trace returned address
        RTN_InsertCall(mallocRtn, IPOINT_AFTER, AFUNPTR(TraceLogger::InsertAllocAddressReturnEntry),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, REG_RAX,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);
        RTN_InsertCall(mallocRtn, IPOINT_AFTER, AFUNPTR(CheckBufferAndStore),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, _entryBufferEndReg,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        RTN_Close(mallocRtn);

        // Show debug info
        cerr << "    RtlAllocateHeap() instrumented." << endl;
    }

    // Find the free() function to log free addresses    
    RTN freeRtn = RTN_FindByName(img, "RtlFreeHeap");
    if(RTN_Valid(freeRtn))
    {
        RTN_Open(freeRtn);

        // Trace address parameter
        RTN_InsertCall(freeRtn, IPOINT_BEFORE, AFUNPTR(TraceLogger::InsertFreeAddressParameterEntry),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_FUNCARG_ENTRYPOINT_VALUE, 2,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);
        RTN_InsertCall(freeRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
            IARG_REG_VALUE, _nextBufferEntryReg,
            IARG_REG_VALUE, _entryBufferEndReg,
            IARG_THREAD_ID,
            IARG_RETURN_REGS, _nextBufferEntryReg,
            IARG_END);

        RTN_Close(freeRtn);

        // Show debug info
        cerr << "    RtlFreeHeap() instrumented." << endl;
    }
}

// Determines whether the given trace entry buffer is full, and stores its contents if neccessary.
TraceEntry *CheckBufferAndStore(TraceEntry *nextEntry, TraceEntry *entryBufferEnd, THREADID tid)
{
    // Only the main thread is instrumented
    if(tid != 0 || nextEntry == NULL || entryBufferEnd == NULL)
        return nextEntry;

    // Buffer full?
    if(TraceLogger::CheckBufferFull(nextEntry, entryBufferEnd))
    {
        // Get trace logger object and store contents
        TraceLogger *traceLogger = static_cast<TraceLogger *>(PIN_GetThreadData(_traceLoggerTlsKey, tid));
        traceLogger->WriteBufferToFile(entryBufferEnd);
        return traceLogger->Begin();
    }
    return nextEntry;
}

// Handles the beginning of a testcase.
TraceEntry *TestcaseStart(ADDRINT newTestcaseId, THREADID tid)
{
    // Get trace logger object and set the new testcase ID
    TraceLogger *traceLogger = static_cast<TraceLogger *>(PIN_GetThreadData(_traceLoggerTlsKey, tid));
    traceLogger->TestcaseStart(static_cast<int>(newTestcaseId));
    return traceLogger->Begin();
}

// Handles the ending of a testcase.
TraceEntry *TestcaseEnd(TraceEntry *nextEntry, THREADID tid)
{
    // Get trace logger object and set the new testcase ID
    TraceLogger *traceLogger = static_cast<TraceLogger *>(PIN_GetThreadData(_traceLoggerTlsKey, tid));
    traceLogger->TestcaseEnd(nextEntry);
    return traceLogger->Begin();
}

// Handles an internal exception of this trace tool.
EXCEPT_HANDLING_RESULT HandlePinToolException(THREADID tid, EXCEPTION_INFO *exceptionInfo, PHYSICAL_CONTEXT *physicalContext, VOID *v)
{
    // Output exception data
    std::cerr << "Internal exception: " << PIN_ExceptionToString(exceptionInfo) << endl;
    return EHR_UNHANDLED;
}

// Converts the given trace entry pointer into its address integer (which is then checked for NULL by Pin).
ADDRINT CheckNextTraceEntryPointerValid(TraceEntry *nextEntry)
{
    return reinterpret_cast<ADDRINT>(nextEntry);
}

// Overwrites the given destination register of the RDRAND instruction with a constant value.
void ChangeRandomNumber(ADDRINT *outputReg)
{
    *outputReg = static_cast<ADDRINT>(_fixedRandomNumber);
}


/****************************************************************************** BEGIN CPUID PATCH ******************************************************************************/


// Changes the output of the CPUID instruction.
void ChangeCpuId(UINT32 inputEax, UINT32 inputEcx, UINT32 *outputEax, UINT32 *outputEbx, UINT32 *outputEcx, UINT32 *outputEdx)
{
    // Modify output depending on requested fields
    if(inputEax == 0)
    {
        // We are on Intel
        *outputEax = _emulatedCpuModelInfo->max_input;
        *outputEbx = 0x756e6547; // Genu
        *outputEdx = 0x49656e69; // ineI
        *outputEcx = 0x6c65746e; // ntel
    }
    else if(inputEax == 1)
    {
        *outputEax = _emulatedCpuModelInfo->encoded_family;
        *outputEdx = _emulatedCpuModelInfo->features_edx;
        *outputEcx = _emulatedCpuModelInfo->features_ecx;
    }
    else if(inputEax == 0x80000000)
    {
        *outputEax = _emulatedCpuModelInfo->max_ext_input;
    }
    else if(inputEax == 0x80000001)
    {
        if(_emulatedCpuModelInfo->max_ext_input >= 0x80000001)
        {
            *outputEdx = _emulatedCpuModelInfo->features_ext_edx;
            *outputEcx = _emulatedCpuModelInfo->features_ext_ecx;
        }
        else
        {
            *outputEdx = 0;
            *outputEcx = 0;
        }
    }
    else if(inputEax == 7 && inputEcx == 0)
    {
        if(_emulatedCpuModelInfo->max_input >= 7)
        {
            *outputEbx = _emulatedCpuModelInfo->features_sext_ebx;
        }
        else
        {
            *outputEbx = 0;
        }
    }
}