// This file is partially copied from DynamoRIO
// Upper part: https://github.com/DynamoRIO/dynamorio/blob/master/core/arch/proc.h
// Lower part: https://github.com/DynamoRIO/dynamorio/blob/master/clients/drcpusim/drcpusim.cpp


/* **********************************************************
* Copyright (c) 2011-2016 Google, Inc.  All rights reserved.
* Copyright (c) 2000-2010 VMware, Inc.  All rights reserved.
* **********************************************************/

/*
* Redistribution and use in source and binary forms, with or without
* modification, are permitted provided that the following conditions are met:
*
* * Redistributions of source code must retain the above copyright notice,
*   this list of conditions and the following disclaimer.
*
* * Redistributions in binary form must reproduce the above copyright notice,
*   this list of conditions and the following disclaimer in the documentation
*   and/or other materials provided with the distribution.
*
* * Neither the name of VMware, Inc. nor the names of its contributors may be
*   used to endorse or promote products derived from this software without
*   specific prior written permission.
*
* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
* AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
* IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
* ARE DISCLAIMED. IN NO EVENT SHALL VMWARE, INC. OR CONTRIBUTORS BE LIABLE
* FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
* DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
* SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
* CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
* LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
* OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH
* DAMAGE.
*/

/* Copyright (c) 2003-2007 Determina Corp. */
/* Copyright (c) 2001-2003 Massachusetts Institute of Technology */
/* Copyright (c) 2000-2001 Hewlett-Packard Company */

/*
* proc.h - processor implementation specific interfaces
*/

#ifndef _PROC_H_
#define _PROC_H_ 1

/* DR_API EXPORT TOFILE dr_proc.h */
/* DR_API EXPORT BEGIN */
/****************************************************************************
* PROCESSOR-SPECIFIC UTILITY ROUTINES AND CONSTANTS
*/
/**
* @file dr_proc.h
* @brief Utility routines for identifying features of the processor.
*/

/**
* The maximum possible required size of floating point state buffer for
* processors with different features (i.e., the processors with the FXSR
* feature on x86, or the processors with the VFPv3 feature on ARM).
* \note The actual required buffer size may vary depending on the processor
* feature.  \note proc_fpstate_save_size() can be used to determine the
* particular size needed.
*/
#ifdef X86
# define DR_FPSTATE_BUF_SIZE 512
#elif defined(ARM) || defined(AARCH64)
/* On ARM/AArch64 proc_save_fpstate saves nothing, so use the smallest
* legal size for an array.
*/
# define DR_FPSTATE_BUF_SIZE 1
#endif

/** The alignment requirements of floating point state buffer. */
#if defined(X86) || defined(AARCH64)
# define DR_FPSTATE_ALIGN  16
#elif defined(ARM)
# define DR_FPSTATE_ALIGN  1
#endif
/** Constants returned by proc_get_vendor(). */
enum
{
    VENDOR_INTEL,   /**< proc_get_vendor() processor identification: Intel */
    VENDOR_AMD,     /**< proc_get_vendor() processor identification: AMD */
    VENDOR_ARM,     /**< proc_get_vendor() processor identification: ARM */
    VENDOR_UNKNOWN, /**< proc_get_vendor() processor identification: unknown */
};

/* Family and Model
*   Intel 486                 Family 4
*   Intel Pentium             Family 5
*   Intel Pentium Pro         Family 6, Model 0 and 1
*   Intel Pentium 2           Family 6, Model 3, 5, and 6
*   Intel Celeron             Family 6, Model 5 and 6
*   Intel Pentium 3           Family 6, Model 7, 8, 10, 11
*   Intel Pentium 4           Family 15, Extended 0
*   Intel Itanium             Family 7
*   Intel Itanium 2           Family 15, Extended 1 and 2
*   Intel Pentium M           Family 6, Model 9 and 13
*   Intel Core                Family 6, Model 14
*   Intel Core 2              Family 6, Model 15
*   Intel Nehalem             Family 6, Models 26 (0x1a), 30 (0x1e), 31 (0x1f)
*   Intel SandyBridge         Family 6, Models 37 (0x25), 42 (0x2a), 44 (0x2c),
*                                              45 (0x2d), 47 (0x2f)
*   Intel IvyBridge           Family 6, Model 58 (0x3a)
*   Intel Atom                Family 6, Model 28 (0x1c), 38 (0x26), 54 (0x36)
*/
/* DR_API EXPORT END */
#ifdef IA32_ON_IA64 /* don't export IA64 stuff! */
/* IA-64 */
# define FAMILY_IA64         7
#endif
/* DR_API EXPORT BEGIN */
/* Remember that we add extended family to family as Intel suggests */
#define FAMILY_LLANO        18 /**< proc_get_family() processor family: AMD Llano */
#define FAMILY_ITANIUM_2_DC 17 /**< proc_get_family() processor family: Itanium 2 DC */
#define FAMILY_K8_MOBILE    17 /**< proc_get_family() processor family: AMD K8 Mobile */
#define FAMILY_ITANIUM_2    16 /**< proc_get_family() processor family: Itanium 2 */
#define FAMILY_K8L          16 /**< proc_get_family() processor family: AMD K8L */
#define FAMILY_K8           15 /**< proc_get_family() processor family: AMD K8 */
#define FAMILY_PENTIUM_4    15 /**< proc_get_family() processor family: Pentium 4 */
#define FAMILY_P4           15 /**< proc_get_family() processor family: P4 family */
#define FAMILY_ITANIUM       7 /**< proc_get_family() processor family: Itanium */
/* Pentium Pro, Pentium II, Pentium III, Athlon, Pentium M, Core, Core 2+ */
#define FAMILY_P6            6 /**< proc_get_family() processor family: P6 family */
#define FAMILY_IVYBRIDGE     6 /**< proc_get_family() processor family: IvyBridge */
#define FAMILY_SANDYBRIDGE   6 /**< proc_get_family() processor family: SandyBridge */
#define FAMILY_NEHALEM       6 /**< proc_get_family() processor family: Nehalem */
#define FAMILY_CORE_I7       6 /**< proc_get_family() processor family: Core i7 */
#define FAMILY_CORE_2        6 /**< proc_get_family() processor family: Core 2 */
#define FAMILY_CORE          6 /**< proc_get_family() processor family: Core */
#define FAMILY_PENTIUM_M     6 /**< proc_get_family() processor family: Pentium M */
#define FAMILY_PENTIUM_3     6 /**< proc_get_family() processor family: Pentium 3 */
#define FAMILY_PENTIUM_2     6 /**< proc_get_family() processor family: Pentium 2 */
#define FAMILY_PENTIUM_PRO   6 /**< proc_get_family() processor family: Pentium Pro */
#define FAMILY_ATHLON        6 /**< proc_get_family() processor family: Athlon */
#define FAMILY_K7            6 /**< proc_get_family() processor family: AMD K7 */
/* Pentium (586) */
#define FAMILY_P5            5 /**< proc_get_family() processor family: P5 family */
#define FAMILY_PENTIUM       5 /**< proc_get_family() processor family: Pentium */
#define FAMILY_K6            5 /**< proc_get_family() processor family: K6 */
#define FAMILY_K5            5 /**< proc_get_family() processor family: K5 */
/* 486 */
#define FAMILY_486           4 /**< proc_get_family() processor family: 486 */

/* We do not enumerate all models; just relevant ones needed to distinguish
* major processors in the same family.
*/
#define MODEL_HASWELL         60 /**< proc_get_model(): Haswell */
#define MODEL_IVYBRIDGE       58 /**< proc_get_model(): Ivybridge */
#define MODEL_I7_WESTMERE_EX  47 /**< proc_get_model(): Sandybridge Westmere Ex */
#define MODEL_SANDYBRIDGE_E   45 /**< proc_get_model(): Sandybridge-E, -EN, -EP */
#define MODEL_I7_WESTMERE     44 /**< proc_get_model(): Westmere */
#define MODEL_SANDYBRIDGE     42 /**< proc_get_model(): Sandybridge */
#define MODEL_I7_CLARKDALE    37 /**< proc_get_model(): Westmere Clarkdale/Arrandale */
#define MODEL_I7_HAVENDALE    31 /**< proc_get_model(): Core i7 Havendale/Auburndale */
#define MODEL_I7_CLARKSFIELD  30 /**< proc_get_model(): Core i7 Clarksfield/Lynnfield */
#define MODEL_ATOM_CEDARVIEW  54 /**< proc_get_model(): Atom Cedarview */
#define MODEL_ATOM_LINCROFT   38 /**< proc_get_model(): Atom Lincroft */
#define MODEL_ATOM            28 /**< proc_get_model(): Atom */
#define MODEL_I7_GAINESTOWN   26 /**< proc_get_model(): Core i7 Gainestown (Nehalem) */
#define MODEL_CORE_PENRYN     23 /**< proc_get_model(): Core 2 Penryn */
#define MODEL_CORE_2          15 /**< proc_get_model(): Core 2 Merom/Conroe */
#define MODEL_CORE_MEROM      15 /**< proc_get_model(): Core 2 Merom */
#define MODEL_CORE            14 /**< proc_get_model(): Core Yonah */
#define MODEL_PENTIUM_M       13 /**< proc_get_model(): Pentium M 2MB L2 */
#define MODEL_PENTIUM_M_1MB    9 /**< proc_get_model(): Pentium M 1MB L2 */


/**
* Feature bits returned by cpuid.  Pass one of these values to proc_has_feature() to
* determine whether the underlying processor has the feature.
*/
typedef enum
{
    /* features returned in edx */
    FEATURE_FPU = 0,              /**< Floating-point unit on chip */
    FEATURE_VME = 1,              /**< Virtual Mode Extension */
    FEATURE_DE = 2,              /**< Debugging Extension */
    FEATURE_PSE = 3,              /**< Page Size Extension */
    FEATURE_TSC = 4,              /**< Time-Stamp Counter */
    FEATURE_MSR = 5,              /**< Model Specific Registers */
    FEATURE_PAE = 6,              /**< Physical Address Extension */
    FEATURE_MCE = 7,              /**< Machine Check Exception */
    FEATURE_CX8 = 8,              /**< #OP_cmpxchg8b supported */
    FEATURE_APIC = 9,              /**< On-chip APIC Hardware supported */
    FEATURE_SEP = 11,             /**< Fast System Call */
    FEATURE_MTRR = 12,             /**< Memory Type Range Registers */
    FEATURE_PGE = 13,             /**< Page Global Enable */
    FEATURE_MCA = 14,             /**< Machine Check Architecture */
    FEATURE_CMOV = 15,             /**< Conditional Move Instruction */
    FEATURE_PAT = 16,             /**< Page Attribute Table */
    FEATURE_PSE_36 = 17,             /**< 36-bit Page Size Extension */
    FEATURE_PSN = 18,             /**< Processor serial # present & enabled */
    FEATURE_CLFSH = 19,             /**< #OP_clflush supported */
    FEATURE_DS = 21,             /**< Debug Store */
    FEATURE_ACPI = 22,             /**< Thermal monitor & SCC supported */
    FEATURE_MMX = 23,             /**< MMX technology supported */
    FEATURE_FXSR = 24,             /**< Fast FP save and restore */
    FEATURE_SSE = 25,             /**< SSE Extensions supported */
    FEATURE_SSE2 = 26,             /**< SSE2 Extensions supported */
    FEATURE_SS = 27,             /**< Self-snoop */
    FEATURE_HTT = 28,             /**< Hyper-threading Technology */
    FEATURE_TM = 29,             /**< Thermal Monitor supported */
    FEATURE_IA64 = 30,             /**< IA64 Capabilities */
    FEATURE_PBE = 31,             /**< Pending Break Enable */
    /* features returned in ecx */
    FEATURE_SSE3 = 0 + 32,         /**< SSE3 Extensions supported */
    FEATURE_PCLMULQDQ = 1 + 32,         /**< #OP_pclmulqdq supported */
    FEATURE_DTES64 = 2 + 32,         /**< 64-bit debug store supported */
    FEATURE_MONITOR = 3 + 32,         /**< #OP_monitor/#OP_mwait supported */
    FEATURE_DS_CPL = 4 + 32,         /**< CPL Qualified Debug Store */
    FEATURE_VMX = 5 + 32,         /**< Virtual Machine Extensions */
    FEATURE_SMX = 6 + 32,         /**< Safer Mode Extensions */
    FEATURE_EST = 7 + 32,         /**< Enhanced Speedstep Technology */
    FEATURE_TM2 = 8 + 32,         /**< Thermal Monitor 2 */
    FEATURE_SSSE3 = 9 + 32,         /**< SSSE3 Extensions supported */
    FEATURE_CID = 10 + 32,        /**< Context ID */
    FEATURE_FMA = 12 + 32,        /**< FMA instructions supported */
    FEATURE_CX16 = 13 + 32,        /**< #OP_cmpxchg16b supported */
    FEATURE_xTPR = 14 + 32,        /**< Send Task Priority Messages */
    FEATURE_PDCM = 15 + 32,        /**< Perfmon and Debug Capability */
    FEATURE_PCID = 17 + 32,        /**< Process-context identifiers */
    FEATURE_DCA = 18 + 32,        /**< Prefetch from memory-mapped devices */
    FEATURE_SSE41 = 19 + 32,        /**< SSE4.1 Extensions supported */
    FEATURE_SSE42 = 20 + 32,        /**< SSE4.2 Extensions supported */
    FEATURE_x2APIC = 21 + 32,        /**< x2APIC supported */
    FEATURE_MOVBE = 22 + 32,        /**< #OP_movbe supported */
    FEATURE_POPCNT = 23 + 32,        /**< #OP_popcnt supported */
    FEATURE_AES = 25 + 32,        /**< AES instructions supported */
    FEATURE_XSAVE = 26 + 32,        /**< OP_xsave* supported */
    FEATURE_OSXSAVE = 27 + 32,        /**< #OP_xgetbv supported in user mode */
    FEATURE_AVX = 28 + 32,        /**< AVX instructions supported */
    FEATURE_F16C = 29 + 32,        /**< 16-bit floating-point conversion supported */
    FEATURE_RDRAND = 30 + 32,        /**< #OP_rdrand supported */
    /* extended features returned in edx */
    FEATURE_SYSCALL = 11 + 64,        /**< #OP_syscall/#OP_sysret supported */
    FEATURE_XD_Bit = 20 + 64,        /**< Execution Disable bit */
    FEATURE_MMX_EXT = 22 + 64,        /**< AMD MMX Extensions */
    FEATURE_PDPE1GB = 26 + 64,        /**< Gigabyte pages */
    FEATURE_RDTSCP = 27 + 64,        /**< #OP_rdtscp supported */
    FEATURE_EM64T = 29 + 64,        /**< Extended Memory 64 Technology */
    FEATURE_3DNOW_EXT = 30 + 64,        /**< AMD 3DNow! Extensions */
    FEATURE_3DNOW = 31 + 64,        /**< AMD 3DNow! instructions supported */
    /* extended features returned in ecx */
    FEATURE_LAHF = 0 + 96,         /**< #OP_lahf/#OP_sahf available in 64-bit mode */
    FEATURE_SVM = 2 + 96,         /**< AMD Secure Virtual Machine */
    FEATURE_LZCNT = 5 + 96,         /**< #OP_lzcnt supported */
    FEATURE_SSE4A = 6 + 96,         /**< AMD SSE4A Extensions supported */
    FEATURE_PRFCHW = 8 + 96,         /**< #OP_prefetchw supported */
    FEATURE_XOP = 11 + 96,         /**< AMD XOP supported */
    FEATURE_SKINIT = 12 + 96,         /**< AMD #OP_skinit/#OP_stgi supported */
    FEATURE_FMA4 = 16 + 96,         /**< AMD FMA4 supported */
    FEATURE_TBM = 21 + 96,         /**< AMD Trailing Bit Manipulation supported */
    /* structured extended features returned in ebx */
    FEATURE_FSGSBASE = 0 + 128,        /**< #OP_rdfsbase, etc. supported */
    FEATURE_BMI1 = 3 + 128,        /**< BMI1 instructions supported */
    FEATURE_HLE = 4 + 128,        /**< Hardware Lock Elision supported */
    FEATURE_AVX2 = 5 + 128,        /**< AVX2 instructions supported */
    FEATURE_BMI2 = 8 + 128,        /**< BMI2 instructions supported */
    FEATURE_ERMSB = 9 + 128,        /**< Enhanced rep movsb/stosb supported */
    FEATURE_INVPCID = 10 + 128,        /**< #OP_invpcid supported */
    FEATURE_RTM = 11 + 128,        /**< Restricted Transactional Memory supported */
} feature_bit_t;

/**
* L1 and L2 cache sizes, used by proc_get_L1_icache_size(),
* proc_get_L1_dcache_size(), proc_get_L2_cache_size(), and
* proc_get_cache_size_str().
*/
#ifdef AVOID_API_EXPORT
/* Make sure to keep this in sync with proc_get_cache_size_str() in proc.c */
#endif
typedef enum
{
    CACHE_SIZE_8_KB,    /**< L1 or L2 cache size of 8 KB. */
    CACHE_SIZE_16_KB,   /**< L1 or L2 cache size of 16 KB. */
    CACHE_SIZE_32_KB,   /**< L1 or L2 cache size of 32 KB. */
    CACHE_SIZE_64_KB,   /**< L1 or L2 cache size of 64 KB. */
    CACHE_SIZE_128_KB,  /**< L1 or L2 cache size of 128 KB. */
    CACHE_SIZE_256_KB,  /**< L1 or L2 cache size of 256 KB. */
    CACHE_SIZE_512_KB,  /**< L1 or L2 cache size of 512 KB. */
    CACHE_SIZE_1_MB,    /**< L1 or L2 cache size of 1 MB. */
    CACHE_SIZE_2_MB,    /**< L1 or L2 cache size of 2 MB. */
    CACHE_SIZE_UNKNOWN  /**< Unknown L1 or L2 cache size. */
} cache_size_t;

/* DR_API EXPORT END */

/* exported for efficient access */
extern size_t cache_line_size;

#define CACHE_LINE_SIZE() cache_line_size

/* xcr0 and xstate_bv feature bits */
enum
{
    XCR0_AVX = 4,
    XCR0_SSE = 2,
    XCR0_FP = 1,
};

#endif /* _PROC_H_ */




# define FEAT(DR_proc_val) (1U << ((FEATURE_##DR_proc_val) % 32))


// Family encoding:
//   ext family | ext model | type  | family | model | stepping
//      27:20   |   19:16   | 13:12 |  11:8  |  7:4  |   3:0
static inline unsigned int cpuid_encode_family(unsigned int family, unsigned int model, unsigned int stepping)
{
    unsigned int ext_family = 0;
    unsigned int ext_model = 0;
    if(family == 6 || family == 15)
    {
        ext_model = model >> 4;
        model = model & 0xf;
    }
    if(family >= 15)
    {
        ext_family = family - 15;
        family = 15;
    }
    ASSERT((stepping & ~0xf) == 0, "CPUID enc: Error in stepping");
    ASSERT((model & ~0xf) == 0, "CPUID enc: Error in model");
    ASSERT((family & ~0xf) == 0, "CPUID enc: Error in family");
    ASSERT((ext_model & ~0xf) == 0, "CPUID enc: Error in ext model");
    return
        (ext_family << 20) |
        (ext_model << 16) |
        // type is 0 == Original OEM
        (family << 8) |
        (model << 4) |
        stepping;
}

typedef struct _cpuid_model_t
{
    unsigned int max_input;
    unsigned int max_ext_input;
    unsigned int encoded_family;
    unsigned int features_edx;
    unsigned int features_ecx;
    unsigned int features_ext_edx;
    unsigned int features_ext_ecx;
    unsigned int features_sext_ebx;
} cpuid_model_t;

static cpuid_model_t model_Pentium3 = {
    3,
    0, // see Pentium comment
    0x672, // == cpuid_encode_family(FAMILY_PENTIUM_3, 7, 2),
    FEAT(FPU) | FEAT(VME) | FEAT(DE) | FEAT(PSE) | FEAT(TSC) | FEAT(MSR) |
    FEAT(MCE) | FEAT(MTRR) | FEAT(MCA) | FEAT(PGE) | FEAT(PAE) |
    FEAT(PSE_36) | FEAT(PAT) |
    // ISA-affecting:
    FEAT(CX8) | FEAT(CMOV) | FEAT(MMX) | FEAT(SEP) | FEAT(FXSR) |
    FEAT(SSE),
    0,
    0,
    0,
    0
};

// XXX: I'm ignoring the eax=6 table (digital thermal sensors)
static cpuid_model_t model_Merom = {
    10,
    0x80000008,
    0x6fb, // == cpuid_encode_family(FAMILY_CORE_2, MODEL_CORE_MEROM, 11),
    FEAT(FPU) | FEAT(VME) | FEAT(DE) | FEAT(PSE) | FEAT(TSC) | FEAT(MSR) |
    FEAT(MCE) | FEAT(MTRR) | FEAT(MCA) | FEAT(PGE) | FEAT(PAE) |
    FEAT(PSE_36) | FEAT(PAT) | FEAT(APIC) | FEAT(DS) | FEAT(SS) |
    FEAT(TM) | FEAT(ACPI) /* no HTT */ | FEAT(PBE) |
    // ISA-affecting:
    FEAT(CX8) | FEAT(CMOV) | FEAT(MMX) | FEAT(SEP) | FEAT(FXSR) |
    FEAT(SSE) | FEAT(SSE2) | FEAT(CLFSH),
    FEAT(DTES64) | FEAT(DS_CPL) | FEAT(CID) | FEAT(xTPR) | FEAT(EST) |
    FEAT(TM2) | FEAT(VMX) | FEAT(SMX) | FEAT(PDCM) |
    // ISA-affecting:
    FEAT(SSE3) | FEAT(MONITOR) | FEAT(CX16) | FEAT(SSSE3),
    FEAT(EM64T) | FEAT(XD_Bit),
    FEAT(LAHF),
    0
};

static cpuid_model_t model_Westmere = {
    10,
    0x80000008,
    0x206c2, // == cpuid_encode_family(FAMILY_CORE_2, MODEL_I7_WESTMERE, 2),
    FEAT(FPU) | FEAT(VME) | FEAT(DE) | FEAT(PSE) | FEAT(TSC) | FEAT(MSR) |
    FEAT(MCE) | FEAT(MTRR) | FEAT(MCA) | FEAT(PGE) | FEAT(PAE) |
    FEAT(PSE_36) | FEAT(PAT) | FEAT(APIC) | FEAT(DS) | FEAT(SS) |
    FEAT(TM) | FEAT(ACPI) | FEAT(HTT) | FEAT(PBE) |
    // ISA-affecting:
    FEAT(CX8) | FEAT(CMOV) | FEAT(MMX) | FEAT(SEP) | FEAT(FXSR) |
    FEAT(SSE) | FEAT(SSE2) | FEAT(CLFSH),
    FEAT(DTES64) | FEAT(DS_CPL) | FEAT(CID) | FEAT(xTPR) | FEAT(EST) |
    FEAT(TM2) | FEAT(VMX) | FEAT(SMX) | FEAT(PDCM) | FEAT(PCID) |
    // ISA-affecting:
    FEAT(SSE3) | FEAT(MONITOR) | FEAT(CX16) | FEAT(SSSE3) | FEAT(SSE41) |
    FEAT(SSE42) | FEAT(POPCNT) | FEAT(AES) | FEAT(PCLMULQDQ),
    FEAT(EM64T) | FEAT(XD_Bit) | FEAT(RDTSCP) | FEAT(PDPE1GB),
    FEAT(LAHF),
    0
};

static cpuid_model_t model_Ivybridge = {
    11,
    0x80000008,
    0x306a9, // == cpuid_encode_family(FAMILY_CORE_2, MODEL_IVYBRIDGE, 9),
    FEAT(FPU) | FEAT(VME) | FEAT(DE) | FEAT(PSE) | FEAT(TSC) | FEAT(MSR) |
    FEAT(MCE) | FEAT(MTRR) | FEAT(MCA) | FEAT(PGE) | FEAT(PAE) |
    FEAT(PSE_36) | FEAT(PAT) | FEAT(APIC) | FEAT(DS) | FEAT(SS) |
    FEAT(TM) | FEAT(ACPI) | FEAT(HTT) | FEAT(PBE) |
    // ISA-affecting:
    FEAT(CX8) | FEAT(CMOV) | FEAT(MMX) | FEAT(SEP) | FEAT(FXSR) |
    FEAT(SSE) | FEAT(SSE2) | FEAT(CLFSH),
    FEAT(DTES64) | FEAT(DS_CPL) | FEAT(CID) | FEAT(xTPR) | FEAT(EST) |
    FEAT(TM2) | FEAT(VMX) | FEAT(SMX) | FEAT(PDCM) | FEAT(PCID) |
    FEAT(x2APIC) |
    // ISA-affecting:
    FEAT(SSE3) | FEAT(MONITOR) | FEAT(CX16) | FEAT(SSSE3) | FEAT(SSE41) |
    FEAT(SSE42) | FEAT(POPCNT) | FEAT(AES) | FEAT(PCLMULQDQ) | FEAT(AVX) |
    FEAT(XSAVE) | FEAT(OSXSAVE) | FEAT(F16C) | FEAT(RDRAND),
    FEAT(EM64T) | FEAT(XD_Bit) | FEAT(RDTSCP), /*no PDPE1GB */
    FEAT(LAHF),
    FEAT(FSGSBASE) | FEAT(ERMSB)
};