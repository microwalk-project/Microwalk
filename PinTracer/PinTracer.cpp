/*
IMPORTANT: The instrumented program or one of its dependencies MUST contain (named) "malloc" and "free" functions.
To get meaningful outputs, make sure that these functions are called with "call" and have a "ret" instruction (no "jmp" to another function).
*/


/* INCLUDES */
#include "TraceWriter.h"
#include <xed-interface.h>
#include "Utilities.h"
#include "CpuOverride.h"


/* GLOBAL VARIABLES */

// The output file command line option.
KNOB<std::string> KnobOutputFilePrefix(KNOB_MODE_WRITEONCE, "pintool", "o", "out", "specify file name/path prefix for trace output");

// The names of interesting images, separated by semicolons.
KNOB<std::string> KnobInterestingImageList(KNOB_MODE_WRITEONCE, "pintool", "i", ".exe", "specify list of interesting images, separated by semicolons");

// The desired CPU feature level.
KNOB<int> KnobCpuFeatureLevel(KNOB_MODE_WRITEONCE, "pintool", "c", "0", "specify desired CPU model: 0 = Default, 1 = Pentium3, 2 = Merom, 3 = Westmere, 4 = Ivybridge (your own CPU should form a superset of the selected option)");

// Constant random number generator value.
// Magic default value is 0xBADBADBADBADBAD (Pin does not provide an API to check whether parameter is actually in the command line).
KNOB<UINT64> KnobFixedRandomNumbers(KNOB_MODE_WRITEONCE, "pintool", "r", "841534158063459245", "set constant output for RDRAND instruction");

// Enable stack allocation tracking.
KNOB<int> KnobEnableStackAllocationTracking(KNOB_MODE_WRITEONCE, "pintool", "s", "0", "enable stack allocation tracking");

// The names of interesting images, parsed from the command line option.
std::vector<std::string> _interestingImages;

// The thread local storage key for the trace logger objects.
TLS_KEY _traceWriterTlsKey;

// The next writable entry buffer position (per thread).
REG _nextBufferEntryReg;

// The end of the entry buffer (per thread).
REG _entryBufferEndReg;

// The EAX input register of a CPUID instruction.
REG _cpuIdEaxInputReg;

// The ECX input register of a CPUID instruction.
REG _cpuIdEcxInputReg;

// Data of loaded images for lookup during trace instrumentation.
std::vector<ImageData*> _images;

// Controls whether RDRAND random numbers are replaced by fixed ones.
bool _useFixedRandomNumber = false;

// Controls whether stack allocation tracking is enabled.
bool _enableStackAllocationTracking = false;

// The fixed random number to be returned after each RDRAND instruction.
UINT64 _fixedRandomNumber = 0;


/* CALLBACK PROTOTYPES */

VOID InstrumentTrace(TRACE trace, VOID* v);
VOID ThreadStart(THREADID tid, CONTEXT* ctxt, INT32 flags, VOID* v);
VOID ThreadFini(THREADID tid, const CONTEXT* ctxt, INT32 code, VOID* v);
VOID InstrumentImage(IMG img, VOID* v);
TraceEntry* CheckBufferAndStore(TraceEntry* nextEntry, TraceEntry* entryBufferEnd, THREADID tid);
TraceEntry* TestcaseStart(ADDRINT newTestcaseId, THREADID tid, TraceEntry* nextEntry);
TraceEntry* TestcaseEnd(TraceEntry* nextEntry, THREADID tid);
EXCEPT_HANDLING_RESULT HandlePinToolException(THREADID tid, EXCEPTION_INFO* exceptionInfo, PHYSICAL_CONTEXT* physicalContext, VOID* v);
ADDRINT CheckNextTraceEntryPointerValid(TraceEntry* nextEntry);
void ChangeRandomNumber(ADDRINT* outputReg);
void ChangeCpuId(UINT32 inputEax, UINT32 inputEcx, UINT32* outputEax, UINT32* outputEbx, UINT32* outputEcx, UINT32* outputEdx);


/* FUNCTIONS */

// The main procedure of the tool.
int main(int argc, char* argv[])
{
	// Initialize PIN library
	if(PIN_Init(argc, argv))
	{
		// Print help message if -h(elp) is specified in the command line or the command line is invalid 
		std::cerr << KNOB_BASE::StringKnobSummary() << std::endl;
		return -1;
	}

	// Split list of interesting images
	std::stringstream interestingImagesStringStream(KnobInterestingImageList);
	std::string item;
	while(std::getline(interestingImagesStringStream, item, ':'))
		if(!item.empty())
		{
			tolower(item);
			_interestingImages.push_back(item);
		}

	// Create trace entry buffer and all associated variables
	_traceWriterTlsKey = PIN_CreateThreadDataKey(0);
	_nextBufferEntryReg = PIN_ClaimToolRegister();
	_entryBufferEndReg = PIN_ClaimToolRegister();

	// Reserve tool registers for CPUID modification
	_cpuIdEaxInputReg = PIN_ClaimToolRegister();
	_cpuIdEcxInputReg = PIN_ClaimToolRegister();

	// Set model for CPU emulation
	SetEmulatedCpu(KnobCpuFeatureLevel.Value());

	// Check if constant random numbers are desired
	if(KnobFixedRandomNumbers.Value() != static_cast<UINT64>(0xBADBADBADBADBAD))
	{
		_useFixedRandomNumber = true;
		_fixedRandomNumber = KnobFixedRandomNumbers.Value();
		std::cerr << "Using fixed RDRAND output " << _fixedRandomNumber << std::endl;
	}

	// Check if stack allocation tracking is enabled
	if(KnobEnableStackAllocationTracking.Value() != 0)
	{
		_enableStackAllocationTracking = true;
		std::cerr << "Stack allocation tracking is enabled" << std::endl;
	}

	// Initialize prefix mode
	TraceWriter::InitPrefixMode(trim(KnobOutputFilePrefix.Value()));

	// Instrument instructions and routines
	IMG_AddInstrumentFunction(InstrumentImage, 0);
	TRACE_AddInstrumentFunction(InstrumentTrace, 0);

	// Set thread event handlers
	PIN_AddThreadStartFunction(ThreadStart, 0);
	PIN_AddThreadFiniFunction(ThreadFini, 0);

	// Handle internal exceptions (for debugging)
	PIN_AddInternalExceptionHandler(HandlePinToolException, NULL);

	// Load symbols to access function name information
	PIN_InitSymbols();

	// Start the target program
	PIN_StartProgram();
	return 0;
}


/* CALLBACKS */

// [Callback] Instruments memory access instructions.
VOID InstrumentTrace(TRACE trace, VOID* v)
{
	// Check each instruction in each basic block
	for(BBL bbl = TRACE_BblHead(trace); BBL_Valid(bbl); bbl = BBL_Next(bbl))
	{
		// Before instrumentation check first whether we are in an interesting image
		ImageData* img = nullptr;
		for(ImageData* i : _images)
			if(i->ContainsBasicBlock(bbl))
			{
				img = i;
				break;
			}
		bool interesting;
		if(img == nullptr)
		{
			// Should not happen, since images should have been loaded before they can be instrumented...
			std::cerr << "Error: Cannot resolve image of basic block " << std::hex << BBL_Address(bbl) << " (instrumenting as 'interesting')" << std::endl;

			// If it _does_ happen for some weird reason, we err on the side of caution here, and don't want to lose potential valuable data
			interesting = true;
		}
		else
		{
			interesting = img->IsInteresting();
		}

		// Run through instructions
		for(INS ins = BBL_InsHead(bbl); INS_Valid(ins); ins = INS_Next(ins))
		{
			// Ignore everything that uses segment registers (shouldn't be used by relevant software parts)
			// Windows e.g. uses GS for thread local storage
			// We also don't support far jumps/call/returns, so tracing programs which make use of those may lead to interesting behavior 
			// TODO Hint that in documentation
			if(INS_SegmentPrefix(ins))
				continue;

			// Ignore frequent and uninteresting instructions to reduce instrumentation time
			OPCODE opc = INS_Opcode(ins);
			if(opc >= XED_ICLASS_PUSH && opc <= XED_ICLASS_PUSHFQ)
				continue;
			if(opc >= XED_ICLASS_POP && opc <= XED_ICLASS_POPFQ)
				continue;
			if(opc == XED_ICLASS_LEA)
				continue;

			// Change CPUID instruction
			if(opc == XED_ICLASS_CPUID)
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

			// Overwrite RDRAND instruction
			if(opc == XED_ICLASS_RDRAND && _useFixedRandomNumber)
			{
				// Modify output register
				INS_InsertCall(ins, IPOINT_AFTER, AFUNPTR(ChangeRandomNumber),
					IARG_REG_REFERENCE, INS_RegW(ins, 0),
					IARG_END);

				continue;
			}

			// TODO potential performance optimization:
			//    The trace buffer should never be full when an instruction generates no new entry,
			//    so the following CheckBuffer... if/then could be merged into the first then call

			// Trace branch instructions (conditional and unconditional)
			if(INS_IsCall(ins) && INS_IsControlFlow(ins))
			{
				// call instructions cannot be instrumented with IPOINT_AFTER, since they do have no fallthrough
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertBranchEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_BRANCH_TARGET_ADDR,
					IARG_BOOL, 1,
					IARG_UINT32, TraceEntryFlags::BranchTypeCall,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::CheckBufferFull),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_THREAD_ID,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);

				// Store stack pointer value
				if(_enableStackAllocationTracking)
				{
					INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckNextTraceEntryPointerValid),
						IARG_REG_VALUE, _nextBufferEntryReg,
						IARG_END);
					INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::InsertStackPointerModificationEntry),
						IARG_REG_VALUE, _nextBufferEntryReg,
						IARG_INST_PTR,
						IARG_REG_VALUE, REG_RSP,
						IARG_UINT32, TraceEntryFlags::StackIsCall,
						IARG_RETURN_REGS, _nextBufferEntryReg,
						IARG_END);
					INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::CheckBufferFull),
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

				continue;
			}
			if(INS_IsBranch(ins) && INS_IsControlFlow(ins))
			{
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertBranchEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_BRANCH_TARGET_ADDR,
					IARG_BRANCH_TAKEN,
					IARG_UINT32, TraceEntryFlags::BranchTypeJump,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::CheckBufferFull),
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
			if(INS_IsRet(ins) && INS_IsControlFlow(ins))
			{
				// ret instructions cannot be instrumented with IPOINT_AFTER, since they do have no fallthrough
				INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckNextTraceEntryPointerValid),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::InsertRetBranchEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_CONTEXT, // TODO why not use IARG_BRANCH_TARGET_ADDR?
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::CheckBufferFull),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckBufferAndStore),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_THREAD_ID,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);

				// Store stack pointer value
				if(_enableStackAllocationTracking)
				{
					INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(CheckNextTraceEntryPointerValid),
						IARG_REG_VALUE, _nextBufferEntryReg,
						IARG_END);
					INS_InsertThenCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::InsertStackPointerModificationEntry),
						IARG_REG_VALUE, _nextBufferEntryReg,
						IARG_INST_PTR,
						IARG_REG_VALUE, REG_RSP,
						IARG_UINT32, TraceEntryFlags::StackIsReturn,
						IARG_RETURN_REGS, _nextBufferEntryReg,
						IARG_END);
					INS_InsertIfCall(ins, IPOINT_TAKEN_BRANCH, AFUNPTR(TraceWriter::CheckBufferFull),
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

				continue;
			}

			// Ignore everything else in uninteresting images
			if(!interesting)
				continue;

			// Stack allocation tracking
			// ret is already tracked above; push/pop are ignored
			if(_enableStackAllocationTracking && INS_FullRegWContain(ins, REG_RSP))
			{
				INS_InsertIfCall(ins, IPOINT_AFTER, AFUNPTR(CheckNextTraceEntryPointerValid),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_AFTER, AFUNPTR(TraceWriter::InsertStackPointerModificationEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_REG_VALUE, REG_RSP,
					IARG_UINT32, TraceEntryFlags::StackIsOther,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_AFTER, AFUNPTR(TraceWriter::CheckBufferFull),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_AFTER, AFUNPTR(CheckBufferAndStore),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_REG_VALUE, _entryBufferEndReg,
					IARG_THREAD_ID,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
			}

			// Trace instructions with memory read
			if(INS_IsMemoryRead(ins) && INS_IsStandardMemop(ins))
			{
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(CheckNextTraceEntryPointerValid),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_END);
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertMemoryReadEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_MEMORYREAD_EA,
					IARG_MEMORYREAD_SIZE,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::CheckBufferFull),
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
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertMemoryReadEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_MEMORYREAD2_EA,
					IARG_MEMORYREAD_SIZE, // NOTE IARG_MEMORYREAD2_SIZE does not exist, but we can assume that both operands have the same size
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::CheckBufferFull),
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
				INS_InsertThenCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertMemoryWriteEntry),
					IARG_REG_VALUE, _nextBufferEntryReg,
					IARG_INST_PTR,
					IARG_MEMORYWRITE_EA,
					IARG_MEMORYWRITE_SIZE,
					IARG_RETURN_REGS, _nextBufferEntryReg,
					IARG_END);
				INS_InsertIfCall(ins, IPOINT_BEFORE, AFUNPTR(TraceWriter::CheckBufferFull),
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
VOID ThreadStart(THREADID tid, CONTEXT* ctxt, INT32 flags, VOID* v)
{
	// Only instrument main thread
	if(tid == 0)
	{
		// Create new trace logger for this thread
		TraceWriter* traceWriter = new TraceWriter(trim(KnobOutputFilePrefix.Value()));

		// Put logger into local storage of this thread
		PIN_SetThreadData(_traceWriterTlsKey, traceWriter, tid);

		// Initialize entry buffer pointers
		PIN_SetContextReg(ctxt, _nextBufferEntryReg, reinterpret_cast<ADDRINT>(traceWriter->Begin()));
		PIN_SetContextReg(ctxt, _entryBufferEndReg, reinterpret_cast<ADDRINT>(traceWriter->End()));
	}
	else
	{
		// Set entry buffer pointers as null pointers
		std::cerr << "Ignoring thread #" << tid << std::endl;
		PIN_SetContextReg(ctxt, _nextBufferEntryReg, 0);
		PIN_SetContextReg(ctxt, _entryBufferEndReg, 0);
	}
}

// [Callback] Cleans up after thread exit.
VOID ThreadFini(THREADID tid, const CONTEXT* ctxt, INT32 code, VOID* v)
{
	// Only the main thread is instrumented
	if(tid != 0)
		return;

	// Finalize trace logger of this thread
	TraceWriter* traceWriter = static_cast<TraceWriter*>(PIN_GetThreadData(_traceWriterTlsKey, tid));
	traceWriter->WriteBufferToFile(reinterpret_cast<TraceEntry*>(PIN_GetContextReg(ctxt, _nextBufferEntryReg)));
	delete traceWriter;
	PIN_SetThreadData(_traceWriterTlsKey, nullptr, tid);
}

// [Callback] Instruments the memory allocation/deallocation functions.
VOID InstrumentImage(IMG img, VOID* v)
{
	// Retrieve image name
	std::string imageName = IMG_Name(img);

	// Check whether image is interesting (its name appears in the image name list passed over the command line)
	std::string imageNameLower = imageName;
	tolower(imageNameLower);
	INT8 interesting = (find_if(_interestingImages.begin(), _interestingImages.end(), [&](std::string& interestingImageName) { return imageNameLower.find(interestingImageName) != std::string::npos; }) != _interestingImages.end()) ? 1 : 0;

	// Retrieve image memory offsets
	UINT64 imageStart = IMG_LowAddress(img);
	UINT64 imageEnd = IMG_HighAddress(img);

	// Record image data
	TraceWriter::WriteImageLoadData(static_cast<int>(interesting), imageStart, imageEnd, imageName);

	// Remember image for filtered trace instrumentation
	_images.push_back(new ImageData(interesting, imageName, imageStart, imageEnd));
	std::cerr << "Image '" << imageName << "' loaded at " << std::hex << imageStart << " ... " << std::hex << imageEnd << (interesting != 0 ? " [interesting]" : "") << std::endl;

	// Find the Pin notification functions to insert testcase markers
	RTN notifyStartRtn = RTN_FindByName(img, "PinNotifyTestcaseStart");
	if(RTN_Valid(notifyStartRtn))
	{
		// Switch to next testcase
		RTN_Open(notifyStartRtn);
		RTN_InsertCall(notifyStartRtn, IPOINT_BEFORE, AFUNPTR(TestcaseStart),
			IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
			IARG_THREAD_ID,
			IARG_REG_VALUE, _nextBufferEntryReg,
			IARG_RETURN_REGS, _nextBufferEntryReg,
			IARG_END);
		RTN_Close(notifyStartRtn);

		std::cerr << "    PinNotifyTestcaseStart() instrumented." << std::endl;
	}
	RTN notifyEndRtn = RTN_FindByName(img, "PinNotifyTestcaseEnd");
	if(RTN_Valid(notifyEndRtn))
	{
		// Close testcase
		RTN_Open(notifyEndRtn);
		RTN_InsertCall(notifyEndRtn, IPOINT_BEFORE, AFUNPTR(TestcaseEnd),
			IARG_REG_VALUE, _nextBufferEntryReg,
			IARG_THREAD_ID,
			IARG_RETURN_REGS, _nextBufferEntryReg,
			IARG_END);
		RTN_Close(notifyEndRtn);

		std::cerr << "    PinNotifyTestcaseEnd() instrumented." << std::endl;
	}

	// Find the Pin stack pointer notification function
	RTN notifyStackPointerRtn = RTN_FindByName(img, "PinNotifyStackPointer");
	if(RTN_Valid(notifyStackPointerRtn))
	{
		// Save stack pointer value
		RTN_Open(notifyStackPointerRtn);
		RTN_InsertCall(notifyStackPointerRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertStackPointerInfoEntry),
			IARG_REG_VALUE, _nextBufferEntryReg,
			IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
			IARG_FUNCARG_ENTRYPOINT_VALUE, 1,
			IARG_RETURN_REGS, _nextBufferEntryReg,
			IARG_END);
		RTN_InsertCall(notifyStackPointerRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
			IARG_REG_VALUE, _nextBufferEntryReg,
			IARG_REG_VALUE, _entryBufferEndReg,
			IARG_THREAD_ID,
			IARG_RETURN_REGS, _nextBufferEntryReg,
			IARG_END);
		RTN_Close(notifyStackPointerRtn);

		std::cerr << "    PinNotifyStackPointer() instrumented." << std::endl;
	}

	// Find allocation and free functions to log allocation sizes and addresses
#if defined(_WIN32)
	RTN mallocRtn = RTN_FindByName(img, "RtlAllocateHeap");
	if(RTN_Valid(mallocRtn))
	{
		// Trace size parameter
		RTN_Open(mallocRtn);
		RTN_InsertCall(mallocRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertHeapAllocSizeParameterEntry),
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
		RTN_InsertCall(mallocRtn, IPOINT_AFTER, AFUNPTR(TraceWriter::InsertHeapAllocAddressReturnEntry),
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

		std::cerr << "    RtlAllocateHeap() instrumented." << std::endl;
	}

	RTN freeRtn = RTN_FindByName(img, "RtlFreeHeap");
	if(RTN_Valid(freeRtn))
	{
		// Trace address parameter
		RTN_Open(freeRtn);
		RTN_InsertCall(freeRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertHeapFreeAddressParameterEntry),
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

		std::cerr << "    RtlFreeHeap() instrumented." << std::endl;
	}
#else
	// Only instrument allocation methods from libc
	if(imageName.find("libc.so") != std::string::npos)
	{
		RTN mallocRtn = RTN_FindByName(img, "malloc");
		if(RTN_Valid(mallocRtn))
		{
			// Trace size parameter
			RTN_Open(mallocRtn);
			RTN_InsertCall(mallocRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertHeapAllocSizeParameterEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(mallocRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);

			// Trace returned address
			RTN_InsertCall(mallocRtn, IPOINT_AFTER, AFUNPTR(TraceWriter::InsertHeapAllocAddressReturnEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCRET_EXITPOINT_VALUE,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(mallocRtn, IPOINT_AFTER, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_Close(mallocRtn);

			std::cerr << "    malloc() instrumented." << std::endl;
		}

		RTN callocRtn = RTN_FindByName(img, "calloc");
		if(RTN_Valid(callocRtn))
		{
			// Trace size parameter
			RTN_Open(callocRtn);
			RTN_InsertCall(callocRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertCallocSizeParameterEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
				IARG_FUNCARG_ENTRYPOINT_VALUE, 1,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(callocRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);

			// Trace returned address
			RTN_InsertCall(callocRtn, IPOINT_AFTER, AFUNPTR(TraceWriter::InsertHeapAllocAddressReturnEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCRET_EXITPOINT_VALUE,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(callocRtn, IPOINT_AFTER, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_Close(callocRtn);

			std::cerr << "    calloc() instrumented." << std::endl;
		}

		RTN reallocRtn = RTN_FindByName(img, "realloc");
		if(RTN_Valid(reallocRtn))
		{
			// Trace size parameter
			RTN_Open(reallocRtn);
			RTN_InsertCall(reallocRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertHeapAllocSizeParameterEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCARG_ENTRYPOINT_VALUE, 1,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(reallocRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);

			// Trace returned address
			RTN_InsertCall(reallocRtn, IPOINT_AFTER, AFUNPTR(TraceWriter::InsertHeapAllocAddressReturnEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCRET_EXITPOINT_VALUE,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(reallocRtn, IPOINT_AFTER, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_Close(reallocRtn);

			std::cerr << "    realloc() instrumented." << std::endl;
		}

		RTN freeRtn = RTN_FindByName(img, "free");
		if(RTN_Valid(freeRtn))
		{
			// Trace address parameter
			RTN_Open(freeRtn);
			RTN_InsertCall(freeRtn, IPOINT_BEFORE, AFUNPTR(TraceWriter::InsertHeapFreeAddressParameterEntry),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_FUNCARG_ENTRYPOINT_VALUE, 0,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_InsertCall(freeRtn, IPOINT_BEFORE, AFUNPTR(CheckBufferAndStore),
				IARG_REG_VALUE, _nextBufferEntryReg,
				IARG_REG_VALUE, _entryBufferEndReg,
				IARG_THREAD_ID,
				IARG_RETURN_REGS, _nextBufferEntryReg,
				IARG_END);
			RTN_Close(freeRtn);

			std::cerr << "    free() instrumented." << std::endl;
		}
	}
#endif
}

// Determines whether the given trace entry buffer is full, and stores its contents if neccessary.
TraceEntry* CheckBufferAndStore(TraceEntry* nextEntry, TraceEntry* entryBufferEnd, THREADID tid)
{
	// Only the main thread is instrumented
	if(tid != 0 || nextEntry == NULL || entryBufferEnd == NULL)
		return nextEntry;

	// Buffer full?
	if(TraceWriter::CheckBufferFull(nextEntry, entryBufferEnd))
	{
		// Get trace logger object and store contents
		TraceWriter* traceWriter = static_cast<TraceWriter*>(PIN_GetThreadData(_traceWriterTlsKey, tid));
		traceWriter->WriteBufferToFile(entryBufferEnd);
		return traceWriter->Begin();
		}
	return nextEntry;
	}

// Handles the beginning of a testcase.
TraceEntry* TestcaseStart(ADDRINT newTestcaseId, THREADID tid, TraceEntry* nextEntry)
{
	// Get trace logger object and set the new testcase ID
	TraceWriter* traceWriter = static_cast<TraceWriter*>(PIN_GetThreadData(_traceWriterTlsKey, tid));
	traceWriter->TestcaseStart(static_cast<int>(newTestcaseId), nextEntry);
	return traceWriter->Begin();
}

// Handles the ending of a testcase.
TraceEntry* TestcaseEnd(TraceEntry* nextEntry, THREADID tid)
{
	// Get trace logger object and set the new testcase ID
	TraceWriter* traceWriter = static_cast<TraceWriter*>(PIN_GetThreadData(_traceWriterTlsKey, tid));
	traceWriter->TestcaseEnd(nextEntry);
	return traceWriter->Begin();
}

// Handles an internal exception of this trace tool.
EXCEPT_HANDLING_RESULT HandlePinToolException(THREADID tid, EXCEPTION_INFO* exceptionInfo, PHYSICAL_CONTEXT* physicalContext, VOID* v)
{
	// Output exception data
	std::cerr << "Internal exception: " << PIN_ExceptionToString(exceptionInfo) << std::endl;
	return EHR_UNHANDLED;
}

// Converts the given trace entry pointer into its address integer (which is then checked for NULL by Pin).
ADDRINT CheckNextTraceEntryPointerValid(TraceEntry* nextEntry)
{
	return reinterpret_cast<ADDRINT>(nextEntry);
}

// Overwrites the given destination register of the RDRAND instruction with a constant value.
void ChangeRandomNumber(ADDRINT* outputReg)
{
	*outputReg = static_cast<ADDRINT>(_fixedRandomNumber);
}