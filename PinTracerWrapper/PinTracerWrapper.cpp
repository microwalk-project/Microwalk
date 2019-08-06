/*
This program is a lightweight wrapper for the investigated library.
In trace mode (using Pin) it periodically reads test input file names from stdin and loads the given testcases.
Note: If the target library is written in C++ and exports mangled names, this program should also be compiled as C++ so it can find those exports.
*/

/* INCLUDES */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

// OS-specific imports and helper macros
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#define _EXPORT __declspec(dllexport)
#define _NOINLINE __declspec(noinline)
#else

#endif

/*** TODO REFERENCE INVESTIGATED LIBRARY ***/


/* TYPES */

#if defined(_WIN32)
// Helper struct to read Thread Information Block.
struct _TEB
{
    NT_TIB tib;
    // Ignore remainder
};
#else

#endif

/* FUNCTIONS */

// Executes the target function.
// This function should only call the investigated library functions, this executable will not analyzed by the fuzzer.
// Do not use global variables, since the fuzzer might reuse the instrumented version of this executable for several different inputs.
_NOINLINE void RunTarget(FILE* input)
{
    /*** TODO INSERT THE LIBRARY CALLING CODE HERE ***/
}

// Notify PIN that a testcase starts/ends.
// These functions (and their names) must not be optimized away by the compiler, so Pin can find and instrument them.
// The return values reduce the probability that the compiler uses these function in other places as no-ops (Visual C++ did do this in some experiments).
#pragma optimize("", off)
extern "C" _EXPORT _NOINLINE int PinNotifyTestcaseStart(int t) { return t + 42; }
extern "C" _EXPORT _NOINLINE int PinNotifyTestcaseEnd() { return 42; }
extern "C" _EXPORT _NOINLINE int PinNotifyStackPointer(uint64_t spMin, uint64_t spMax) { return (int)(spMin + spMax + 42); }
#pragma optimize("", on)

// Reads the stack pointer base value and transmits it to Pin.
void ReadAndSendStackPointer()
{
#if defined(_WIN32)
    // Read stack pointer
    _TEB* threadEnvironmentBlock = NtCurrentTeb();
    PinNotifyStackPointer(reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackLimit), reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackBase));
#else

#endif
}

// Main trace target function. The following actions are performed:
//     The current action is read from stdin.
//     A line with "t" followed by a numeric ID, and another line with a file path determining a new testcase, that is subsequently loaded and fed into the target function, while calling PinNotifyNextFile() beforehand.
//     A line with "e 0" terminates the program.
extern "C" _EXPORT void TraceFunc(int argc, const char** argv)
{
    // Run until exit is requested
    char inputBuffer[512];
    char errBuffer[128];
    while(true)
    {
        // First transmit stack pointer information
        ReadAndSendStackPointer();

        // Read command and testcase ID (0 for exit command)
        char command;
        int testcaseId;
        gets_s(inputBuffer, sizeof(inputBuffer));
        sscanf_s(inputBuffer, "%c %d", &command, 1, &testcaseId);

        // Exit or process given testcase
        if(command == 'e')
            break;
        else if(command == 't')
        {
            // Read testcase file name
            gets_s(inputBuffer, sizeof(inputBuffer));

            // Load testcase file and run target function
            FILE* inputFile;
            errno_t err = fopen_s(&inputFile, inputBuffer, "rb");
            if(inputFile == NULL)
            {
                strerror_s(errBuffer, sizeof(errBuffer), err);
                fprintf(stderr, "Error opening input file '%s': %s\n", inputBuffer, errBuffer);
                continue;
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
    TraceFunc(argc, argv);
    return 0;
}