/*
This program is a lightweight wrapper for the investigated library.
In trace mode (using Pin) it periodically reads test input file names from stdin and loads the given testcases.

Note: If the target library is written in C++ and exports mangled names, this program should also be compiled as C++ so it can find those exports.
      If the target library is written in C, do not forget to use `extern "C"` when including the headers.
     
Some functions have _EXPORT annotations, even though they are not used externally. This ensures that the function name is included in the binary,
which helps reading the resulting call tree.
*/

// Switch for benchmarking code.
// Uncommenting this enables a target which is designed to generate large traces, which take a long preprocessing and analysis time.
#define BENCHMARK

/* INCLUDES */

// OS-specific imports and helper macros
#if defined(_WIN32)
    #define _CRT_SECURE_NO_DEPRECATE
    
    #define WIN32_LEAN_AND_MEAN
    #include <Windows.h>
    
    #define _EXPORT __declspec(dllexport)
    #define _NOINLINE __declspec(noinline)
#else
    #ifdef _GNU_SOURCE
        #undef _GNU_SOURCE
    #endif

    #define _EXPORT __attribute__((visibility("default")))
    #define _NOINLINE __attribute__((noinline))
    
    #include <sys/resource.h>
#endif

// *** TODO REFERENCE INVESTIGATED LIBRARY [
#if defined(BENCHMARK)

#elif defined(_WIN32)
    #pragma comment(lib, "bcrypt.lib")
    #include <bcrypt.h>
#else
    #include <openssl/evp.h>
#endif
// ] ***

// Standard includes
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cerrno>


/* TYPES */

#if defined(_WIN32)
    // Helper struct to read Thread Information Block.
    struct _TEB
    {
        NT_TIB tib;
        // Ignore remainder
    };
#else
    // Empty
#endif

/* FUNCTIONS */

// Performs target initialization steps.
// This function is called once in the very beginning, to make sure that the target is entirely loaded.
// The call is included into the trace prefix.
_EXPORT _NOINLINE void InitTarget()
{
	// *** TODO INSERT THE TARGET INITIALIZATION CODE HERE [
	
    // Simple targets for testing
#if defined(BENCHMARK)

#elif defined(_WIN32)
	BCRYPT_ALG_HANDLE dummy;
	BCryptOpenAlgorithmProvider(&dummy, BCRYPT_AES_ALGORITHM, nullptr, 0);
	BCryptCloseAlgorithmProvider(dummy, 0);
#else

#endif
	
    // ] ***
}

// Executes the target function.
// This function should only call the investigated library functions, this executable will not analyzed by the fuzzer.
// Do not use global variables, since the fuzzer might reuse the instrumented version of this executable for several different inputs.
_EXPORT _NOINLINE void RunTarget(FILE* input)
{
    // *** TODO INSERT THE LIBRARY CALLING CODE HERE [
    
    // Simple targets for testing
#if defined(BENCHMARK)

    uint8_t data[32];
    if(fread(data, 1, 32, input) != 32)
        return;

    int* buffer = static_cast<int *>(calloc(256, sizeof(int)));
    for(int i = 0; i < 1024 * 256; ++i)
        buffer[data[i % 32]] = i;

#elif defined(_WIN32)
	BYTE secret_key[16];
	if(fread(secret_key, 1, 16, input) != 16)
		return;

	BYTE plain[16];
	if(fread(plain, 1, 16, input) != 16)
		return;

	BCRYPT_ALG_HANDLE aesAlg;
	BCRYPT_KEY_HANDLE aesKey;
	BCryptOpenAlgorithmProvider(&aesAlg, BCRYPT_AES_ALGORITHM, nullptr, 0);
	DWORD keyObjectSize;
	DWORD data;
	BCryptGetProperty(aesAlg, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&keyObjectSize), sizeof(DWORD), &data, 0);

	BYTE* keyObj = static_cast<BYTE*>(malloc(keyObjectSize));
	DWORD blockLength;
	BCryptGetProperty(aesAlg, BCRYPT_BLOCK_LENGTH, reinterpret_cast<PUCHAR>(&blockLength), sizeof(DWORD), &data, 0);
	BCryptSetProperty(aesAlg, BCRYPT_CHAINING_MODE, PUCHAR(BCRYPT_CHAIN_MODE_ECB), sizeof(BCRYPT_CHAIN_MODE_ECB), 0);
	BCryptGenerateSymmetricKey(aesAlg, &aesKey, keyObj, keyObjectSize, static_cast<PUCHAR>(secret_key), sizeof(secret_key), 0);

	DWORD cipherTextSize;
	BCryptEncrypt(aesKey, plain, sizeof(plain), nullptr, nullptr, 0, nullptr, 0, &cipherTextSize, 0);
	BYTE* cipherText = static_cast<BYTE*>(malloc(cipherTextSize));
	BCryptEncrypt(aesKey, plain, sizeof(plain), nullptr, nullptr, 0, cipherText, cipherTextSize, &data, 0);
#else
    uint8_t secret_key[16];
    if(fread(secret_key, 1, 16, input) != 16)
        return;

    uint8_t plain[16];
    if(fread(plain, 1, 16, input) != 16)
        return;

    EVP_CIPHER_CTX* ctx = EVP_CIPHER_CTX_new();
    EVP_EncryptInit_ex(ctx, EVP_aes_128_cbc(), nullptr, secret_key, nullptr);

    uint8_t cipher[16];
    int len;
    EVP_EncryptUpdate(ctx, cipher, &len, plain, 16);

    EVP_CIPHER_CTX_free(ctx);

    //BIO_dump_fp(stderr, reinterpret_cast<const char *>(cipher), 16);
#endif
	
    // ] ***
}

// Pin notification functions.
// These functions (and their names) must not be optimized away by the compiler, so Pin can find and instrument them.
// The return values reduce the probability that the compiler uses these function in other places as no-ops (Visual C++ did do this in some experiments).
#pragma optimize("", off)
extern "C" _EXPORT _NOINLINE int PinNotifyTestcaseStart(int t) { return t + 42; }
extern "C" _EXPORT _NOINLINE int PinNotifyTestcaseEnd() { return 42; }
extern "C" _EXPORT _NOINLINE int PinNotifyStackPointer(uint64_t spMin, uint64_t spMax) { return static_cast<int>(spMin + spMax + 42); }
#pragma optimize("", on)

// Reads the stack pointer base value and transmits it to Pin.
_EXPORT void ReadAndSendStackPointer()
{
#if defined(_WIN32)
    // Read stack pointer
    _TEB* threadEnvironmentBlock = NtCurrentTeb();
    PinNotifyStackPointer(reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackLimit), reinterpret_cast<uint64_t>(threadEnvironmentBlock->tib.StackBase));
#else
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

    uint64_t stackMin = reinterpret_cast<uint64_t>(stackBase) - reinterpret_cast<uint64_t>(stackLimit.rlim_cur);
    uint64_t stackMax = (reinterpret_cast<uint64_t>(stackBase) + 0x10000) & ~0x10000ull; // Round to next higher multiple of 64 kB (should be safe on x86 systems)
    PinNotifyStackPointer(stackMin, stackMax);
#endif
}

// Main trace target function. The following actions are performed:
//     The current action is read from stdin.
//     A line with "t" followed by a numeric ID, and another line with a file path determining a new testcase, that is subsequently loaded and fed into the target function, while calling PinNotifyNextFile() beforehand.
//     A line with "e 0" terminates the program.
extern "C" _EXPORT void TraceFunc()
{
	// First transmit stack pointer information
	ReadAndSendStackPointer();

	// Initialize target library
	InitTarget();

    // Run until exit is requested
    char inputBuffer[512];
    char errBuffer[128];
    while(true)
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
            if(inputFile == nullptr)
            {
#if defined(_WIN32)
                strerror_s(errBuffer, sizeof(errBuffer), errno);
#else
                strerror_r(errno, errBuffer, sizeof(errBuffer));
#endif
                fprintf(stderr, "Error opening input file '%s': [%d] %s\n", inputBuffer, errno, errBuffer);
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
    TraceFunc();
    return 0;
}