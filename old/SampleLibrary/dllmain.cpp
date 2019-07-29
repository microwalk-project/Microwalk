#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdlib.h>
#include <time.h>

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
	// Initialize random number generator
    srand(time(0));
	return TRUE;
}

