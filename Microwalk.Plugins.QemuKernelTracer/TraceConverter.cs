using System.Globalization;
using System.Text.RegularExpressions;
using ElfTools;
using ElfTools.Chunks;
using ElfTools.Enums;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using YamlDotNet.RepresentationModel;

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
                        ulong offset = address - symbolEntry.Value;
                        minAddress += offset;
                        maxAddress += offset;

                        // Store corresponding image data
                        await outputPrefixDataWriter.WriteLineAsync($"i\t1\t{minAddress}\t{maxAddress}\t{_kernelFilePath}");
                    }
                    else
                    {
                        // Section name
                    }
                }

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

    private void ProcessRawTrace(string inputFilePath, string outputFilePath)
    {
    }

    protected override Task InitAsync(YamlMappingNode? moduleOptions)
    {
        // Check trace input directory
        var inputDirectoryPath = moduleOptions.GetChildNodeWithKey("input-directory")?.GetNodeString() ?? throw new ConfigurationException("Missing input directory.");
        _inputDirectory = new DirectoryInfo(inputDirectoryPath);
        if(!_inputDirectory.Exists)
            throw new ConfigurationException("Could not find input directory.");

        // Kernel ELF
        _kernelFilePath = moduleOptions.GetChildNodeWithKey("kernel")?.GetNodeString() ?? throw new ConfigurationException("Missing kernel path.");
        _kernelElf = ElfReader.Load(_kernelFilePath);
        
        // Kernel module ELF
        _kernelModuleFilePath = moduleOptions.GetChildNodeWithKey("kernel-module")?.GetNodeString() ?? throw new ConfigurationException("Missing kernel module path.");
        _kernelModuleElf = ElfReader.Load(_kernelModuleFilePath);

        return Task.CompletedTask;
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
    }
}