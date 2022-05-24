#ifdef _GNU_SOURCE
    #undef _GNU_SOURCE
#endif

#include <sys/resource.h>

#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <errno.h>


// Performs target initialization steps.
// This function is called once in the very beginning for the first testcase file, to make sure that the target is entirely loaded.
// The call is included in the trace prefix.
extern void InitTarget(FILE* input);

// Executes the target function.
// Do not use global variables, since the trace generator will reuse the instrumented version of this executable for several different inputs.
extern void RunTarget(FILE* input);


// Pin notification functions.
// These functions (and their names) must not be optimized away by the compiler, so Pin can find and instrument them.
// The return values reduce the probability that the compiler uses these function in other places as no-ops (Visual C++ did do this in some experiments).
#pragma optimize("", off)
int PinNotifyTestcaseStart(int t) { return t + 42; }
int PinNotifyTestcaseEnd() { return 42; }
int PinNotifyStackPointer(uint64_t spMin, uint64_t spMax) { return (int)(spMin + spMax + 42); }
int PinNotifyAllocation(uint64_t address, uint64_t size) { return (int)(address + 23 * size); }
#pragma optimize("", on)

// Reads the stack pointer base value and transmits it to Pin.
void ReadAndSendStackPointer()
{
    // There does not seem to be a reliable way to get the stack size, so we use an estimation
    // Compiling with -fno-split-stack may be desired, to avoid surprises during analysis

    // Take the current stack pointer as base value
    uintptr_t stackBase;
    asm("mov %%rsp, %0" : "=r"(stackBase));

    // Get full stack size
    struct rlimit stackLimit;
    if(getrlimit(RLIMIT_STACK, &stackLimit) != 0)
    {
        char errBuffer[128];
        strerror_r(errno, errBuffer, sizeof(errBuffer));
        fprintf(stderr, "Error reading stack limit: [%d] %s\n", errno, errBuffer);
    }

    uint64_t stackMin = (uint64_t)stackBase - (uint64_t)stackLimit.rlim_cur;
    uint64_t stackMax = ((uint64_t)stackBase + 0x10000) & ~0xFFFFull; // Round to next higher multiple of 64 kB (should be safe on x86 systems)
    PinNotifyStackPointer(stackMin, stackMax);
}

// Main trace target function. The following actions are performed:
//     The current action is read from stdin.
//     A line with "t" followed by a numeric ID, and another line with a file path determining a new testcase, that is subsequently loaded and fed into the target function, while calling PinNotifyNextFile() beforehand.
//     A line with "e 0" terminates the program.
void TraceFunc()
{
    // First transmit stack pointer information
    ReadAndSendStackPointer();
	
	PinNotifyAllocation((uint64_t)&errno, 8);

    // Run until exit is requested
    char inputBuffer[512];
    char errBuffer[128];
	int targetInitialized = 0;
    while(1)
    {
        // Read command and testcase ID (0 for exit command)
        char command;
        int testcaseId;
        fgets(inputBuffer, sizeof(inputBuffer), stdin);
        sscanf(inputBuffer, "%c %d", &command, &testcaseId);

        // Exit or process given testcase
        if(command == 'e')
            break;
        if(command == 't')
        {
            // Read testcase file name
            fgets(inputBuffer, sizeof(inputBuffer), stdin);
            int inputFileNameLength = strlen(inputBuffer);
            if(inputFileNameLength > 0 && inputBuffer[inputFileNameLength - 1] == '\n')
                inputBuffer[inputFileNameLength - 1] = '\0';

            // Load testcase file and run target function
            FILE* inputFile = fopen(inputBuffer, "rb");
            if(!inputFile)
            {
                strerror_r(errno, errBuffer, sizeof(errBuffer));
                fprintf(stderr, "Error opening input file '%s': [%d] %s\n", inputBuffer, errno, errBuffer);
                continue;
            }
			
			// If the target was not yet initialized, call the init function for the first test case
			if(!targetInitialized)
			{
				InitTarget(inputFile);
				fseek(inputFile, 0, SEEK_SET);
				targetInitialized = 1;
			}
            
            PinNotifyTestcaseStart(testcaseId);
            RunTarget(inputFile);
            PinNotifyTestcaseEnd();
            
            fclose(inputFile);
        }
    }
}

// Wrapper entry point.
int main(int argc, const char** argv)
{
    // Run target function
    TraceFunc();
    return 0;
}