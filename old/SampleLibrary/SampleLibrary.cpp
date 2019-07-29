/*
This small library contains functions to test the LeakageDetector's capabilities.
*/

/* INCLUDES */
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <time.h>
#include <intrin.h>


/* FUNCTIONS */

__declspec(dllexport) uint32_t LeakInputBits(const unsigned char *input, int inputLength)
{
    // Generate some random lookup table
    uint32_t S[256];
    for(int i = 0; i < 256; ++i)
        S[i] = static_cast<uint32_t>(rand());

    // Calculate result by input-dependent access of lookup table entries
    uint32_t result = 0;
    for(int i = 0; i < inputLength; ++i)
            result += S[input[i]];
    return result;
}