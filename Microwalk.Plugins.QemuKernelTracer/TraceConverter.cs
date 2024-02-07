using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ElfTools;
using ElfTools.Chunks;
using ElfTools.Enums;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;

namespace Microwalk.Plugins.QemuKernelTracer;

[FrameworkModule("qemu-converter", "Converts existing raw QEMU traces to Pin-compatible ones.")]
public class TraceConverter : TraceStage
{
    public override bool SupportsParallelism => true;

    private DirectoryInfo _inputDirectory = null!;

    private string _kernelModuleFilePath = null!;
    private ElfFile _kernelModuleElf = null!;

    private string _kernelFilePath = null!;
    private ElfFile _kernelElf = null!;

    /// <summary>
    /// Determines whether the next incoming test case is the first one.
    /// </summary>
    private bool _firstTestcase = true;

    /// <summary>
    /// Protects the first test case variable.
    /// </summary>
    private readonly SemaphoreSlim _firstTestcaseSemaphore = new(1, 1);

    /// <summary>
    /// Base address for the translated module sections.
    /// </summary>
    private ulong _translatedModuleBaseAddress = 0x4200000000;

    /// <summary>
    /// Mapping of module section addresses in memory to linear, translated addresses which work with Microwalk's MAP file infrastructure.
    /// </summary>
    readonly List<(ulong oldBaseAddress, ulong oldEndAddress, ulong offset)> _moduleSectionsTranslations = new();

    public override async Task GenerateTraceAsync(TraceEntity traceEntity)
    {
        // First test case?
        await _firstTestcaseSemaphore.WaitAsync();
        try
        {
            if(_firstTestcase)
            {
                // Handle prefix
                string prefixDataFilePath = Path.Combine(_inputDirectory.FullName, "prefix_data_qemu.txt");
                string outputPrefixDataFilePath = Path.Combine(_inputDirectory.FullName, "prefix_data.txt");
                string tracePrefixFilePath = Path.Combine(_inputDirectory.FullName, "prefix.qemu.trace");
                string outputTracePrefixFilePath = Path.Combine(_inputDirectory.FullName, "prefix.trace");

                // Open output prefix data file
                await using var outputPrefixDataWriter = new StreamWriter(File.Open(outputPrefixDataFilePath, FileMode.Create, FileAccess.Write));

                // Read input prefix data file
                ulong moduleBaseAddress = 0;
                List<(ulong address, string name)> moduleSections = new();
                var kernelAddressRegex = new Regex("\\[KERNEL:([^\\]]+)\\]");
                foreach(var line in await File.ReadAllLinesAsync(prefixDataFilePath))
                {
                    string[] parts = line.Split('\t');

                    ulong address = ulong.Parse(parts[0], NumberStyles.HexNumber);
                    string name = parts[1];

                    Match match;
                    if(name == "[BASE]")
                        moduleBaseAddress = address;
                    else if((match = kernelAddressRegex.Match(name)).Success)
                    {
                        // Symbol in kernel ELF
                        string symbolName = match.Groups[1].Value;

                        // Find symbol table
                        int symbolTableSectionIndex = _kernelElf.SectionHeaderTable.SectionHeaders.FindIndex(h => h.Type == SectionType.SymbolTable);
                        if(symbolTableSectionIndex < 0)
                            throw new Exception("Could not find symbol table section header.");
                        var symbolTableHeader = _kernelElf.SectionHeaderTable.SectionHeaders[symbolTableSectionIndex];
                        var symbolTableChunkIndex = _kernelElf.GetChunkAtFileOffset(symbolTableHeader.FileOffset);
                        if(symbolTableChunkIndex == null)
                            throw new Exception("Could not find symbol table section chunk.");
                        var symbolTableChunk = (SymbolTableChunk)_kernelElf.Chunks[symbolTableChunkIndex.Value.chunkIndex];

                        // Find string table
                        int stringTableSectionIndex = (int)symbolTableHeader.Link;
                        if(stringTableSectionIndex < 0)
                            throw new Exception("Could not find symbol table section header.");
                        var stringTableHeader = _kernelElf.SectionHeaderTable.SectionHeaders[stringTableSectionIndex];
                        var stringTableChunkIndex = _kernelElf.GetChunkAtFileOffset(stringTableHeader.FileOffset);
                        if(stringTableChunkIndex == null)
                            throw new Exception("Could not find string table section chunk.");
                        var stringTableChunk = (StringTableChunk)_kernelElf.Chunks[stringTableChunkIndex.Value.chunkIndex];

                        // Find symbol
                        var symbolEntry = symbolTableChunk.Entries.FirstOrDefault(symbolEntry => stringTableChunk.GetString(symbolEntry.Name) == symbolName);
                        if(symbolEntry == null)
                            throw new Exception($"Could not find kernel symbol '{symbolName}' in kernel symbol table.");

                        // Determine kernel addresses as specified in the ELF program headers
                        ulong minAddress = _kernelElf.ProgramHeaderTable!.ProgramHeaders.Min(ph => ph.VirtualMemoryAddress);
                        ulong maxAddress = _kernelElf.ProgramHeaderTable.ProgramHeaders.Max(ph => ph.VirtualMemoryAddress + ph.MemorySize);

                        // Adjust addresses
                        unchecked // We are explicitly fine with overflows here
                        {
                            ulong offset = address - symbolEntry.Value;
                            minAddress += offset;
                            maxAddress += offset;
                        }

                        // Store corresponding image data
                        await outputPrefixDataWriter.WriteLineAsync($"i\t1\t{minAddress:x16}\t{maxAddress:x16}\t{_kernelFilePath}");
                    }
                    else
                    {
                        // Section name
                        moduleSections.Add((address, name));
                    }
                }

                // Get section name string table
                var sectionNameStringTableHeader = _kernelModuleElf.SectionHeaderTable.SectionHeaders[_kernelModuleElf.Header.SectionHeaderStringTableIndex];
                var sectionNameStringTableChunkIndex = _kernelModuleElf.GetChunkAtFileOffset(sectionNameStringTableHeader.FileOffset);
                if(sectionNameStringTableChunkIndex == null)
                    throw new Exception("Could not find section name string table chunk.");
                var sectionNameStringTableChunk = (StringTableChunk)_kernelModuleElf.Chunks[sectionNameStringTableChunkIndex.Value.chunkIndex];

                // Build mapping of sections to representation in file order
                ulong currentNewAddress = _translatedModuleBaseAddress;
                Dictionary<string, (ulong address, ulong size)> sectionOffsets = new();
                foreach(var sectionHeader in _kernelModuleElf.SectionHeaderTable.SectionHeaders)
                {
                    // Only handle PROGBITS and NOBITS sections, as only those appear in the generated MAP file
                    if(sectionHeader.Type != SectionType.ProgBits && sectionHeader.Type != SectionType.NoBits)
                        continue;

                    // Align current address, if requested by section header
                    if((currentNewAddress & (sectionHeader.Alignment - 1)) != 0)
                        currentNewAddress = (currentNewAddress + sectionHeader.Alignment) & ~(sectionHeader.Alignment - 1);

                    // Record section
                    sectionOffsets.Add(sectionNameStringTableChunk.GetString(sectionHeader.NameStringTableOffset), (currentNewAddress, sectionHeader.Size));

                    // Increase offset by section size
                    currentNewAddress += sectionHeader.Size;
                }

                // Build translation of loaded sections
                foreach(var moduleSection in moduleSections)
                {
                    // Find corresponding translated section address
                    if(!sectionOffsets.TryGetValue(moduleSection.name, out var translatedSectionData))
                    {
                        await Logger.LogWarningAsync($"Could not compute mapping for kernel module section {moduleSection.name}, skipping");
                        continue;
                    }

                    // Compute addend for moving from actual address to translated one
                    ulong offset = unchecked(translatedSectionData.address - moduleSection.address);
                    _moduleSectionsTranslations.Add((moduleSection.address, moduleSection.address + translatedSectionData.size, offset));
                }

                // Store image data for kernel module
                await outputPrefixDataWriter.WriteLineAsync($"i\t1\t{_translatedModuleBaseAddress:x16}\t{currentNewAddress:x16}\t{_kernelModuleFilePath}");

                // Process trace file
                ProcessRawTrace(tracePrefixFilePath, outputTracePrefixFilePath);

                _firstTestcase = false;
            }
        }
        finally
        {
            _firstTestcaseSemaphore.Release();
        }

        // Try to deduce trace file from testcase ID
        string qemuTraceFilePath = Path.Combine(_inputDirectory.FullName, $"t{traceEntity.Id}.qemu.trace");
        if(!File.Exists(qemuTraceFilePath))
        {
            await Logger.LogErrorAsync($"Could not find QEMU trace file for #{traceEntity.Id}.");
            throw new FileNotFoundException("Could not find QEMU trace file.", qemuTraceFilePath);
        }

        string outputTraceFilePath = Path.Combine(_inputDirectory.FullName, $"t{traceEntity.Id}.trace");

        // Process trace file
        ProcessRawTrace(qemuTraceFilePath, outputTraceFilePath);

        traceEntity.RawTraceFilePath = outputTraceFilePath;
    }

    private unsafe void ProcessRawTrace(string inputFilePath, string outputFilePath)
    {
        // Read entire QEMU trace file into memory, since these files should not get too big
        byte[] traceFile = File.ReadAllBytes(inputFilePath);
        int traceFileLength = traceFile.Length;
        int rawTraceEntrySize = Marshal.SizeOf(typeof(RawTraceEntry));

        // Analyze and rewrite entries
        fixed(byte* inputFilePtr = traceFile)
        {
            for(long pos = 0; pos < traceFileLength; pos += rawTraceEntrySize)
            {
                var rawTraceEntry = (RawTraceEntry*)&inputFilePtr[pos];
                switch(rawTraceEntry->Type)
                {
                    case RawTraceEntryTypes.Branch:
                    case RawTraceEntryTypes.MemoryRead:
                    case RawTraceEntryTypes.MemoryWrite:
                    {
                        // Translate addresses
                        rawTraceEntry->Param1 = TranslateAddress(rawTraceEntry->Param1);
                        rawTraceEntry->Param2 = TranslateAddress(rawTraceEntry->Param2);

                        break;
                    }
                }
            }
        }

        // Store modified trace file
        File.WriteAllBytes(outputFilePath, traceFile);
    }

    /// <summary>
    /// Checks whether the given address belongs to a kernel module section and translates it accordingly.
    /// </summary>
    /// <param name="address">Address to be translated.</param>
    /// <returns>The translated address, if it is in a kernel module section; else, the original address.</returns>
    private ulong TranslateAddress(ulong address)
    {
        // Check module addresses
        foreach(var translation in _moduleSectionsTranslations)
        {
            if(translation.oldBaseAddress <= address && address < translation.oldEndAddress)
                return address + translation.offset;
        }

        return address;
    }

    protected override Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        // Check trace input directory
        var inputDirectoryPath = moduleOptions.GetChildNodeOrDefault("input-directory")?.AsString() ?? throw new ConfigurationException("Missing input directory.");
        _inputDirectory = new DirectoryInfo(inputDirectoryPath);
        if(!_inputDirectory.Exists)
            throw new ConfigurationException("Could not find input directory.");

        // Kernel ELF
        _kernelFilePath = moduleOptions.GetChildNodeOrDefault("kernel")?.AsString() ?? throw new ConfigurationException("Missing kernel path.");
        _kernelElf = ElfReader.Load(_kernelFilePath);

        // Kernel module ELF
        _kernelModuleFilePath = moduleOptions.GetChildNodeOrDefault("kernel-module")?.AsString() ?? throw new ConfigurationException("Missing kernel module path.");
        _kernelModuleElf = ElfReader.Load(_kernelModuleFilePath);

        // Translated kernel module base address
        _translatedModuleBaseAddress = moduleOptions.GetChildNodeOrDefault("kernel-module-translated-address")?.AsUnsignedLongHex() ?? throw new ConfigurationException("Missing kernel module section translation base address.");

        return Task.CompletedTask;
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
    }

    #region Adapted from PinTracePreprocessor

    /// <summary>
    /// One trace entry, as present in the trace files.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    internal ref struct RawTraceEntry
    {
        /// <summary>
        /// The type of this entry.
        /// </summary>
        public RawTraceEntryTypes Type;

        /// <summary>
        /// Flag.
        /// Used with: Branch.
        /// </summary>
        public byte Flag;

        /// <summary>
        /// (Padding for reliable parsing by analysis programs)
        /// </summary>
        private byte _padding1;

        /// <summary>
        /// The size of a memory access.
        /// Used with: MemoryRead, MemoryWrite
        /// </summary>
        public short Param0;

        /// <summary>
        /// The address of the instruction triggering the trace entry creation, or the size of an allocation.
        /// Used with: MemoryRead, MemoryWrite, Branch, HeapAllocSizeParameter, StackPointerInfo.
        /// </summary>
        public ulong Param1;

        /// <summary>
        /// The accessed/passed memory address.
        /// Used with: MemoryRead, MemoryWrite, HeapAllocAddressReturn, HeapFreeAddressParameter, Branch, StackPointerInfo.
        /// </summary>
        public ulong Param2;
    }

    /// <summary>
    /// The different types of trace entries, as used in the raw trace files.
    /// </summary>
    internal enum RawTraceEntryTypes : uint
    {
        /// <summary>
        /// A memory read access.
        /// </summary>
        MemoryRead = 1,

        /// <summary>
        /// A memory write access.
        /// </summary>
        MemoryWrite = 2,

        /// <summary>
        /// The size parameter of an allocation (typically malloc).
        /// </summary>
        HeapAllocSizeParameter = 3,

        /// <summary>
        /// The return address of an allocation (typically malloc).
        /// </summary>
        HeapAllocAddressReturn = 4,

        /// <summary>
        /// The address parameter of a deallocation (typically free).
        /// </summary>
        HeapFreeAddressParameter = 5,

        /// <summary>
        /// A code branch.
        /// </summary>
        Branch = 6,

        /// <summary>
        /// Stack pointer information.
        /// </summary>
        StackPointerInfo = 7,

        /// <summary>
        /// A modification of the stack pointer.
        /// </summary>
        StackPointerModification = 8
    }

    #endregion
}