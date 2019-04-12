/*
This program is a lightweight wrapper for the investigated library.
In fuzzing mode it automatically loads the test input files provided by WinAFL, and stores the content in *.testcase files.
In trace mode (using Pin) it periodically reads test input file names from stdin and loads the given testcases.
Note: If the target library is written in C++ and exports mangled names, this program should also be compiled as C++ so it can find those exports.
*/

/* INCLUDES */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include "md5.h"


/*** IMPORT INVESTIGATED LIBRARY ***/
#pragma comment(lib, "SampleLibrary.lib")
__declspec(dllimport) uint32_t LeakInputBits(const unsigned char *input, int inputLength);


/* GLOBALS */

// Handle of the output pipe.
HANDLE _pipe = INVALID_HANDLE_VALUE;


/* TYPES */

// Helper struct to read Thread Information Block.
struct _TEB
{
    NT_TIB tib;
    // Ignore remainder
};


/* FUNCTIONS */

// Executes the target function.
// This function should only call the investigated library functions, this executable will not analyzed by the fuzzer.
// Do not use global variables, since the fuzzer might reuse the instrumented version of this executable for several different inputs.
__declspec(noinline) void RunTarget(FILE *input)
{
    /*** INSERT THE LIBRARY CALLING CODE HERE ***/

    // Leak input bytes
    unsigned char data[16];
    if(fread(data, 1, sizeof(data), input) <= 0)
        return;
    LeakInputBits(data, sizeof(data));
}

// Notify PIN that a testcase starts/ends.
// These functions (and their names) must not be optimized away by the compiler, so Pin can find and instrument them.
// The return values reduce the probability that the compiler uses these function in other places as no-ops (Visual C++ did do this in some experiments).
#pragma optimize("", off)
extern "C" __declspec(dllexport) __declspec(noinline) int PinNotifyTestcaseStart(int t) { return t + 42; }
extern "C" __declspec(dllexport) __declspec(noinline) int PinNotifyTestcaseEnd() { return 42; }
extern "C" __declspec(dllexport) __declspec(noinline) int PinNotifyStackPointer(uint64_t spMin, uint64_t spMax) { return spMin + spMax + 42; }
#pragma optimize("", on)

// This function should be used as the fuzzing target (inclusion of function name "Fuzz" is ensured).
//
// In fuzzing mode:
//     First the current input file is copied into the given testcase directory using a unique name; then the target function is called.
// In trace mode:
//     The current action is read from stdin.
//     A line with "t" followed by a numeric ID, and another line with a file path determining a new testcase, that is subsequently loaded and fed into the target function, while calling PinNotifyNextFile() beforehand.
//     A line with "e 0" terminates the program.
//
// Command line parameters:
// -> mode: The run mode of this program. 1 for fuzzing mode, 2 for trace mode.
// -> [Only in fuzzing mode] inputFile: The name of the file containing input data.
// -> [Only in fuzzing mode] outputTestcaseDirectory: The name of the directory where the testcases shall be stored.
extern "C" __declspec(dllexport) void Fuzz(int argc, const char **argv)
{
    // Determine whether we are in fuzzing or in trace mode
    int mode = atoi(argv[1]);
    if(mode == 1)
    {
        // Read input file
        // Use OS API for that, since C provides no standard way to reliably get a file's size
        HANDLE inputFileOsHandle = CreateFileA(argv[2], GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        DWORD inputFileSize = GetFileSize(inputFileOsHandle, NULL);
        char *inputFileContents = (char *)malloc(inputFileSize + 1);
        ReadFile(inputFileOsHandle, inputFileContents, inputFileSize, &inputFileSize, NULL);
        inputFileContents[inputFileSize] = '\0';
        CloseHandle(inputFileOsHandle);

        // Use MD5 hash function to generate unique testcase file name
        MD5_CTX context;
        MD5_Init(&context);
        MD5_Update(&context, inputFileContents, inputFileSize);
        unsigned char hash[16];
        MD5_Final(hash, &context);
        char testcaseFileName[33]; // 32 chars hash, new line/termininating 0
        for(int i = 0; i < 16; ++i)
            sprintf_s(&testcaseFileName[2 * i], 3, "%02X", hash[i]);
        testcaseFileName[32] = '\0';
        char testcaseFilePath[512];
        strcpy_s(testcaseFilePath, sizeof(testcaseFilePath), argv[3]);
        strcat_s(testcaseFilePath, sizeof(testcaseFilePath) - strlen(testcaseFilePath), "\\");
        strcat_s(testcaseFilePath, sizeof(testcaseFilePath) - strlen(testcaseFilePath), testcaseFileName);
        strcat_s(testcaseFilePath, sizeof(testcaseFilePath) - strlen(testcaseFilePath), ".testcase");

        // Store file contents and notify caller, if the file does not yet exist
        if(GetFileAttributesA(testcaseFilePath) == INVALID_FILE_ATTRIBUTES)
        {
            // Save testcase file
            FILE *testcaseFile;
            fopen_s(&testcaseFile, testcaseFilePath, "wb");
            fwrite(inputFileContents, 1, inputFileSize, testcaseFile);
            fclose(testcaseFile);

            // Notify main application of the new testcase
            if(_pipe != INVALID_HANDLE_VALUE)
            {
                // Append new line character to separate filenames
                // A terminating 0 is not needed, since WriteFile takes a buffer length
                testcaseFileName[32] = '\n';

                // Write into pipe
                DWORD pipeBytesWritten;
                WriteFile(_pipe, testcaseFileName, 33, &pipeBytesWritten, NULL);
            }
        }

        // Call target function for fuzzing
        FILE *inputFile;
        fopen_s(&inputFile, argv[2], "rb");
        RunTarget(inputFile);
        fclose(inputFile);
    }
    else if(mode == 2)
    {
        // Run until exit is requested
        char inputBuffer[512];
        while(true)
        {
            // Read stack pointer
            _TEB *threadEnvironmentBlock = NtCurrentTeb();
            PinNotifyStackPointer(reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackLimit), reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackBase));

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
                FILE *inputFile;
                fopen_s(&inputFile, inputBuffer, "rb");
                PinNotifyTestcaseStart(testcaseId);
                RunTarget(inputFile);
                PinNotifyTestcaseEnd();
                fclose(inputFile);
            }
        }
    }
}

// Wrapper entry point.
// Creates a pipe connection to the main application and starts the fuzzing function.
int main(int argc, const char **argv)
{
    // Fuzzing mode?
    int mode = atoi(argv[1]);
    if(mode == 1)
    {
        // Create pipe
        int retryCount = 6;
        while((_pipe = CreateFileA("\\\\.\\pipe\\LeakageDetectorFuzzingPipe", GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL)) == INVALID_HANDLE_VALUE)
        {
            // Do a fixed number of retries
            if(retryCount == 0)
            {
                FILE *errorFile;
                fopen_s(&errorFile, "FuzzingWrapper.error.txt", "a");
                fprintf(errorFile, "Error: Could not create pipe, error code 0x%08x.\n", GetLastError());
                fclose(errorFile);
                return -1;
            }
            else
                --retryCount;

            // Wait some time until the parent process is ready again
            Sleep(500);
        }
    }

    // Run Fuzzer target function
    Fuzz(argc, argv);

    // The fuzzing target function is instrumented and executed repeatedly without ever returning execution to this function; clean up pipe anyway for debugging purposes
    if(_pipe != INVALID_HANDLE_VALUE)
        CloseHandle(_pipe);
    return 0;
}