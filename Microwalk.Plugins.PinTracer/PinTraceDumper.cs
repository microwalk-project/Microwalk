using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.Plugins.PinTracer;

[FrameworkModule("pin-dump", "Dumps raw Pin trace files in a human-readable form.")]
public class PinTraceDumper : PreprocessorStage
{
    /// <summary>
    /// The trace dump output directory.
    /// </summary>
    private DirectoryInfo _outputDirectory = null!;

    /// <summary>
    /// Image list.
    /// </summary>
    private ImageInfo[] _imageFiles = Array.Empty<ImageInfo>();

    /// <summary>
    /// MAP file collection for resolving symbol names.
    /// </summary>
    private MapFileCollection _mapFileCollection = null!;

    /// <summary>
    /// Determines whether the next incoming test case is the first one.
    /// </summary>
    private bool _firstTestcase = true;

    /// <summary>
    /// Protects the first test case variable.
    /// </summary>
    private readonly SemaphoreSlim _firstTestcaseSemaphore = new(1, 1);

    public override bool SupportsParallelism => true;

    public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
    {
        // Input check
        if(traceEntity.RawTraceFilePath == null)
            throw new Exception("Raw trace file path is null. Is the trace stage missing?");

        // Output file
        string outputFileName = Path.Combine(_outputDirectory.FullName, $"{Path.GetFileName(traceEntity.RawTraceFilePath)}.txt");
        await using var outputStream = File.Open(outputFileName, FileMode.Create);
        await using var outputWriter = new StreamWriter(outputStream);

        // Base path of raw trace files
        string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath) ?? throw new Exception($"Could not determine directory: {traceEntity.RawTraceFilePath}");

        string prefixDataFilePath = Path.Combine(rawTraceFileDirectory, "prefix_data.txt");

        // First test case? -> Read image data
        // We do this on a best-effort basis, as this is a debugging tool - if anything breaks, simply ignore it
        await _firstTestcaseSemaphore.WaitAsync();
        try
        {
            if(_firstTestcase)
            {
                string[] imageDataLines = await File.ReadAllLinesAsync(prefixDataFilePath);

                List<ImageInfo> imageFiles = new();
                int nextImageFileId = 0;
                foreach(string line in imageDataLines)
                {
                    string[] imageData = line.Split('\t');
                    var imageFile = new ImageInfo
                    (
                        nextImageFileId++, // Id
                        ulong.Parse(imageData[2], NumberStyles.HexNumber), // StartAddress
                        ulong.Parse(imageData[3], NumberStyles.HexNumber), // EndAddress
                        Path.GetFileName(imageData[4]), // Name
                        byte.Parse(imageData[1]) != 0 // Interesting
                    );
                    imageFiles.Add(imageFile);
                }

                _imageFiles = imageFiles.OrderByDescending(i => i.Interesting).ToArray();

                _firstTestcase = false;
            }
        }
        catch(Exception ex)
        {
            await Logger.LogErrorAsync($"[pin-dump:{traceEntity.Id}] Can't parse image information: {ex.Message}");
        }
        finally
        {
            _firstTestcaseSemaphore.Release();
        }

        // Write image data
        await outputWriter.WriteLineAsync("-- Image data --");
        await outputWriter.WriteLineAsync(await File.ReadAllTextAsync(prefixDataFilePath));

        // Write prefix
        await outputWriter.WriteLineAsync("-- Trace prefix --");
        DumpRawFile(Path.Combine(rawTraceFileDirectory, "prefix.trace"), outputWriter, $"[pin-dump:{traceEntity.Id}:prefix]");

        // Write trace
        await outputWriter.WriteLineAsync("-- Trace --");
        DumpRawFile(traceEntity.RawTraceFilePath, outputWriter, $"[pin-dump:{traceEntity.Id}]");
    }

    /// <summary>
    /// Loads the given raw trace and converts it into text format.
    /// </summary>
    /// <param name="fileName">Raw trace file.</param>
    /// <param name="outputWriter">Output stream writer.</param>
    /// <param name="logPrefix">Short prefix for log messages printed by this function.</param>
    /// <returns></returns>
    private unsafe void DumpRawFile(string fileName, StreamWriter outputWriter, string logPrefix)
    {
        // Read entire trace file into memory
        byte[] inputFile = File.ReadAllBytes(fileName);
        int inputFileLength = inputFile.Length;
        int rawTraceEntrySize = Marshal.SizeOf(typeof(PinTracePreprocessor.RawTraceEntry));

        // Dump trace entries
        fixed(byte* inputFilePtr = inputFile)
            for(long pos = 0; pos < inputFileLength; pos += rawTraceEntrySize)
            {
                // Read entry
                var rawTraceEntry = *(PinTracePreprocessor.RawTraceEntry*)&inputFilePtr[pos];

                // Write string representation
                switch(rawTraceEntry.Type)
                {
                    case PinTracePreprocessor.RawTraceEntryTypes.HeapAllocSizeParameter:
                    {
                        outputWriter.WriteLine($"AllocSize: {(uint)rawTraceEntry.Param1:x8}");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.HeapAllocAddressReturn:
                    {
                        outputWriter.WriteLine($"AllocReturn: {rawTraceEntry.Param2:x16}");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.HeapFreeAddressParameter:
                    {
                        outputWriter.WriteLine($"HeapFree: {rawTraceEntry.Param2:x16}");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.StackPointerInfo:
                    {
                        outputWriter.WriteLine($"StackPtr: {rawTraceEntry.Param1:x16} {rawTraceEntry.Param2:x16}");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.Branch:
                    {
                        var flags = (PinTracePreprocessor.RawTraceBranchEntryFlags)rawTraceEntry.Flag;

                        bool taken = (flags & PinTracePreprocessor.RawTraceBranchEntryFlags.Taken) != 0;

                        string formattedSourceAddress = rawTraceEntry.Param1.ToString("x16");
                        string formattedDestinationAddress = rawTraceEntry.Param2.ToString("x16");
                        string formattedTaken = taken ? "[taken]" : "[not taken]";
                        if(_mapFileCollection != null)
                        {
                            var sourceImage = FindImage(rawTraceEntry.Param1);
                            if(sourceImage != null)
                                formattedSourceAddress = $"{_mapFileCollection.FormatAddress(sourceImage.Id, sourceImage.Name, (uint)(rawTraceEntry.Param1 - sourceImage.StartAddress))} [{formattedSourceAddress}]";

                            var destinationImage = FindImage(rawTraceEntry.Param2);
                            if(destinationImage != null)
                                formattedDestinationAddress = $"{_mapFileCollection.FormatAddress(destinationImage.Id, destinationImage.Name, (uint)(rawTraceEntry.Param2 - destinationImage.StartAddress))} [{formattedDestinationAddress}]";
                        }

                        var rawBranchType = flags & PinTracePreprocessor.RawTraceBranchEntryFlags.BranchEntryTypeMask;
                        switch(rawBranchType)
                        {
                            case PinTracePreprocessor.RawTraceBranchEntryFlags.Jump:
                                outputWriter.WriteLine($"Jump: {formattedSourceAddress} -> {formattedDestinationAddress} {formattedTaken}");
                                break;

                            case PinTracePreprocessor.RawTraceBranchEntryFlags.Call:
                                outputWriter.WriteLine($"Call: {formattedSourceAddress} -> {formattedDestinationAddress} {formattedTaken}");
                                break;

                            case PinTracePreprocessor.RawTraceBranchEntryFlags.Return:
                                outputWriter.WriteLine($"Return: {formattedSourceAddress} -> {formattedDestinationAddress} {formattedTaken}");
                                break;

                            default:
                                Logger.LogErrorAsync($"{logPrefix} Unspecified instruction type on branch {formattedSourceAddress} -> {formattedDestinationAddress}, skipping").Wait();
                                break;
                        }

                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.MemoryRead:
                    {
                        string formattedInstructionAddress = rawTraceEntry.Param1.ToString("x16");
                        if(_mapFileCollection != null)
                        {
                            var instructionImage = FindImage(rawTraceEntry.Param1);
                            if(instructionImage != null)
                                formattedInstructionAddress = $"{_mapFileCollection.FormatAddress(instructionImage.Id, instructionImage.Name, (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress))} [{formattedInstructionAddress}]";
                        }

                        outputWriter.WriteLine($"MemoryRead: {formattedInstructionAddress} reads {rawTraceEntry.Param2:x16} ({rawTraceEntry.Param0} bytes)");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.MemoryWrite:
                    {
                        string formattedInstructionAddress = rawTraceEntry.Param1.ToString("x16");
                        if(_mapFileCollection != null)
                        {
                            var instructionImage = FindImage(rawTraceEntry.Param1);
                            if(instructionImage != null)
                                formattedInstructionAddress = $"{_mapFileCollection.FormatAddress(instructionImage.Id, instructionImage.Name, (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress))} [{formattedInstructionAddress}]";
                        }

                        outputWriter.WriteLine($"MemoryWrite: {formattedInstructionAddress} writes {rawTraceEntry.Param2:x16} ({rawTraceEntry.Param0} bytes)");
                        break;
                    }

                    case PinTracePreprocessor.RawTraceEntryTypes.StackPointerModification:
                    {
                        string formattedInstructionAddress = rawTraceEntry.Param1.ToString("x16");
                        if(_mapFileCollection != null)
                        {
                            var instructionImage = FindImage(rawTraceEntry.Param1);
                            if(instructionImage != null)
                                formattedInstructionAddress = $"{_mapFileCollection.FormatAddress(instructionImage.Id, instructionImage.Name, (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress))} [{formattedInstructionAddress}]";
                        }

                        var flags = (PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags)rawTraceEntry.Flag;

                        var instructionType = flags & PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.InstructionTypeMask;
                        if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Call)
                            outputWriter.WriteLine($"StackMod: {formattedInstructionAddress} sets RSP = {rawTraceEntry.Param2:x16} (call)");
                        else if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Return)
                            outputWriter.WriteLine($"StackMod: {formattedInstructionAddress} sets RSP = {rawTraceEntry.Param2:x16} (ret)");
                        else if(instructionType == PinTracePreprocessor.RawTraceStackPointerModificationEntryFlags.Other)
                            outputWriter.WriteLine($"StackMod: {formattedInstructionAddress} sets RSP = {rawTraceEntry.Param2:x16} (other)");
                        else
                        {
                            Logger.LogErrorAsync($"{logPrefix} Unspecified instruction type on stack pointer modification, skipping").Wait();
                        }

                        break;
                    }
                }
            }
    }

    /// <summary>
    /// Finds the image that contains the given address.
    /// </summary>
    /// <param name="address">The address to be searched.</param>
    /// <returns></returns>
    private ImageInfo? FindImage(ulong address)
    {
        // Find image by linear search; the image count is expected to be rather small
        // Images are sorted by "interesting" status, to reduce number of loop iterations
        foreach(var img in _imageFiles)
        {
            if(img.StartAddress <= address)
            {
                // Check end address
                if(img.EndAddress >= address)
                    return img;
            }
        }

        return null;
    }

    protected override async Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        // Output directory
        string outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("Missing output directory.");
        _outputDirectory = Directory.CreateDirectory(outputDirectoryPath);

        // Load MAP files
        _mapFileCollection = new MapFileCollection(Logger);
        var mapFilesNode = moduleOptions.GetChildNodeOrDefault("map-files");
        if(mapFilesNode is ListNode mapFileListNode)
        {
            foreach(var mapFileNode in mapFileListNode.Children)
                await _mapFileCollection.LoadMapFileAsync(mapFileNode.AsString() ?? throw new ConfigurationException("Invalid node type in map file list."));
        }

        var mapDirectory = moduleOptions.GetChildNodeOrDefault("map-directory")?.AsString();
        if(mapDirectory != null)
        {
            foreach(var mapFile in Directory.EnumerateFiles(mapDirectory, "*.map"))
                await _mapFileCollection.LoadMapFileAsync(mapFile);
        }
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
    }

    private record ImageInfo(int Id, ulong StartAddress, ulong EndAddress, string Name, bool Interesting);
}