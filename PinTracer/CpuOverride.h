#pragma once
/*
Contains functions to override the CPUID command.
*/

/* INCLUDES */

#include <pin.H>

/* FUNCTIONS */

// Sets the emulated CPU.
void SetEmulatedCpu(int id);

// Changes the output of the CPUID instruction.
void ChangeCpuId(UINT32 inputEax, UINT32 inputEcx, UINT32* outputEax, UINT32* outputEbx, UINT32* outputEcx, UINT32* outputEdx);