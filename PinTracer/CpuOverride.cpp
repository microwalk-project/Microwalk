/* INCLUDES */

#include "CpuOverride.h"
#include "CpuFeatureDefinitions.h"


/* VARIABLES */

// Determines whether the CPU feature level is changed at all.
bool _emulateCpuModel = true;

// The emulated CPU.
static cpuid_model_t* _emulatedCpuModelInfo = nullptr;


/* FUNCTIONS */

void SetEmulatedCpu(int id)
{
    // Set model
    switch(id)
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
}

void ChangeCpuId(UINT32 inputEax, UINT32 inputEcx, UINT32* outputEax, UINT32* outputEbx, UINT32* outputEcx, UINT32* outputEdx)
{
    // Do emulation?
    if(!_emulateCpuModel)
        return;
    
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