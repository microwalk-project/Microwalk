using System.Collections.Concurrent;
using System.Text;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.Plugins.JavascriptTracer;

[FrameworkModule("js", "Preprocesses JavaScript traces.")]
public class JsTracePreprocessor : PreprocessorStage
{
    public override bool SupportsParallelism => true;

    /// <summary>
    /// Determines whether preprocessed traces are stored to disk.
    /// </summary>
    private bool _storeTraces;

    /// <summary>
    /// The preprocessed trace output directory.
    /// </summary>
    private DirectoryInfo? _outputDirectory;

    /// <summary>
    /// The MAP file output directory.
    /// </summary>
    private DirectoryInfo _mapDirectory = null!;

    /// <summary>
    /// Number of bits for column numbers encoded into an 32-bit integer.
    /// </summary>
    private int _columnsBits = 13;

    private uint _columnMask;

    /// <summary>
    /// Determines whether the next incoming test case is the first one.
    /// </summary>
    private bool _firstTestcase = true;

    /// <summary>
    /// Protects the first test case variable.
    /// </summary>
    private readonly SemaphoreSlim _firstTestcaseSemaphore = new(1, 1);

    /// <summary>
    /// Trace prefix data.
    /// </summary>
    private TracePrefixFile _tracePrefix = null!;

    /// <summary>
    /// Next heap allocation offset after processing the trace prefix.
    /// </summary>
    private ulong _prefixNextHeapAllocationAddress = 0;

    /// <summary>
    /// Heap allocations from the trace prefix.
    /// </summary>
    private Dictionary<int, HeapObjectData>? _prefixHeapObjects = null;

    /// <summary>
    /// Compressed lines from the trace prefix, indexed by line ID.
    /// </summary>
    private Dictionary<int, string>? _prefixCompressedLinesLookup = null;

    /// <summary>
    /// ID of the external functions image.
    /// </summary>
    private int _externalFunctionsImageId;

    /// <summary>
    /// Image data of external functions ("E:&lt;function name&gt;").
    /// </summary>
    private TracePrefixFile.ImageFileInfo _externalFunctionsImage = null!;

    /// <summary>
    /// Metadata about loaded "images" (= scripts), indexed by script IDs.
    /// </summary>
    private Dictionary<int, TracePrefixFile.ImageFileInfo> _imageFiles = null!;

    /// <summary>
    /// The next address which is assigned to an external function.
    /// </summary>
    private uint _currentExternalFunctionAddress = 1;

    /// <summary>
    /// Lookup for addresses assigned to external functions "[extern]:functionName", indexed by functionName.
    /// </summary>
    private readonly ConcurrentDictionary<string, uint> _externalFunctionAddresses = new();

    /// <summary>
    /// Lookup for all encoded relative addresses. Indexed by image ID and encoding ("script.js:1:2:1:3").
    /// </summary>
    private readonly Dictionary<int, ConcurrentDictionary<string, (uint start, uint end)>> _relativeAddressLookup = new();

    /// <summary>
    /// For each image ID, maps encoded start and end addresses to function names.
    /// </summary>
    private readonly Dictionary<int, ConcurrentDictionary<(uint start, uint end), string>> _functionNameLookup = new();

    /// <summary>
    /// Requested MAP entries, which will be generated after preprocessing is done.
    /// This is dictionary is used as a set, so the value is always ignored.
    /// </summary>
    private readonly ConcurrentDictionary<(int imageId, uint relativeAddress), object?> _requestedMapEntries = new();

    public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
    {
        // Input check
        if(traceEntity.RawTraceFilePath == null)
            throw new Exception("Raw trace file path is null. Is the trace stage missing?");

        // First test case?
        await _firstTestcaseSemaphore.WaitAsync();
        try
        {
            if(_firstTestcase)
            {
                await Logger.LogDebugAsync("[preprocess] Preprocessing prefix");

                // Paths
                string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath) ?? throw new Exception($"Could not determine directory: {traceEntity.RawTraceFilePath}");
                string scriptsFilePath = Path.Combine(rawTraceFileDirectory, "scripts.txt");
                string tracePrefixFilePath = Path.Combine(rawTraceFileDirectory, "prefix.trace");

                // Read scripts data and translate into image file format
                string[] scriptsFileLines = await File.ReadAllLinesAsync(scriptsFilePath);
                int maxImageNameLength = 8; // [extern]
                List<TracePrefixFile.ImageFileInfo> imageFiles = new();
                foreach(string line in scriptsFileLines)
                {
                    string[] scriptData = line.Split('\t');
                    int imageFileId = int.Parse(scriptData[0]);
                    var imageFile = new TracePrefixFile.ImageFileInfo
                    {
                        Id = imageFileId,
                        Interesting = true,
                        StartAddress = (ulong)imageFileId << 32,
                        EndAddress = ((ulong)imageFileId << 32) | 0xFFFFFFFFul,
                        Name = scriptData[2]
                    };
                    imageFiles.Add(imageFile);
                    _relativeAddressLookup.Add(imageFile.Id, new ConcurrentDictionary<string, (uint start, uint end)>());
                    _functionNameLookup.Add(imageFile.Id, new ConcurrentDictionary<(uint start, uint end), string>());

                    if(imageFile.Name.Length > maxImageNameLength)
                        maxImageNameLength = imageFile.Name.Length;
                }

                // Add dummy image for [extern] functions
                _externalFunctionsImageId = imageFiles.Max(i => i.Id) + 1;
                _externalFunctionsImage = new TracePrefixFile.ImageFileInfo
                {
                    Id = _externalFunctionsImageId,
                    Interesting = true,
                    StartAddress = (ulong)_externalFunctionsImageId << 32,
                    EndAddress = ((ulong)_externalFunctionsImageId << 32) | 0xFFFFFFFFul,
                    Name = "[extern]"
                };
                imageFiles.Add(_externalFunctionsImage);
                _relativeAddressLookup.Add(_externalFunctionsImage.Id, new ConcurrentDictionary<string, (uint start, uint end)>());
                _functionNameLookup.Add(_externalFunctionsImage.Id, new ConcurrentDictionary<(uint start, uint end), string>());

                // Prepare writer for serializing trace data
                using var tracePrefixFileWriter = new FastBinaryWriter(imageFiles.Count * (32 + maxImageNameLength));

                // Write image files
                tracePrefixFileWriter.WriteInt32(imageFiles.Count);
                foreach(var imageFile in imageFiles)
                    imageFile.Store(tracePrefixFileWriter);

                // Load and parse trace prefix data
                _imageFiles = imageFiles.ToDictionary(i => i.Id);
                await PreprocessFileAsync(tracePrefixFilePath, true, tracePrefixFileWriter, "[preprocess:prefix]");

                // Create trace prefix object
                var preprocessedTracePrefixData = tracePrefixFileWriter.Buffer.AsMemory(0, tracePrefixFileWriter.Length);
                _tracePrefix = new TracePrefixFile(preprocessedTracePrefixData);
                _firstTestcase = false;

                // Store to disk?
                if(_storeTraces)
                {
                    string outputPath = Path.Combine(_outputDirectory!.FullName, "prefix.trace.preprocessed");
                    await using var writer = new BinaryWriter(File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None));
                    writer.Write(preprocessedTracePrefixData.Span);
                }
            }
        }
        finally
        {
            _firstTestcaseSemaphore.Release();
        }

        // Preprocess trace data
        using var traceFileWriter = new FastBinaryWriter(1);
        await Logger.LogDebugAsync($"[preprocess:{traceEntity.Id}] Preprocessing trace");
        await PreprocessFileAsync(traceEntity.RawTraceFilePath, false, traceFileWriter, $"[preprocess:{traceEntity.Id}]");

        // Create trace file object
        var preprocessedTraceData = traceFileWriter.Buffer.AsMemory(0, traceFileWriter.Length);
        var preprocessedTraceFile = new TraceFile(_tracePrefix, preprocessedTraceData);

        // Store to disk?
        if(_storeTraces)
        {
            traceEntity.PreprocessedTraceFilePath = Path.Combine(_outputDirectory!.FullName, Path.GetFileName(traceEntity.RawTraceFilePath) + ".preprocessed");
            await using var writer = new BinaryWriter(File.Open(traceEntity.PreprocessedTraceFilePath, FileMode.Create, FileAccess.Write, FileShare.None));
            writer.Write(preprocessedTraceData.Span);
        }

        // Keep trace data in memory for the analysis stages
        traceEntity.PreprocessedTraceFile = preprocessedTraceFile;
    }

    private async Task PreprocessFileAsync(string inputFileName, bool isPrefix, FastBinaryWriter traceFileWriter, string logPrefix)
    {
        // Read entire raw trace into memory, for faster processing
        string[] inputFile = await File.ReadAllLinesAsync(inputFileName, Encoding.UTF8, PipelineToken);

        // Resize output buffer to avoid re-allocations
        // As an upper bound, we just take the number of raw trace entries
        traceFileWriter.ResizeBuffer(MaxPreprocessedTraceEntrySize * inputFile.Length);

        // Parse trace entries
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastRet1Entry = null;
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastCondEntry = null;
        Dictionary<int, HeapObjectData> heapObjects = _prefixHeapObjects == null ? new() : new(_prefixHeapObjects);
        Dictionary<int, string> compressedLinesLookup = _prefixCompressedLinesLookup == null ? new() : new(_prefixCompressedLinesLookup);
        ulong nextHeapAllocationAddress = _prefixNextHeapAllocationAddress;
        const uint heapAllocationChunkSize = 0x100000;
        bool lastWasCond = false;
        int lastLineId = 0;
        foreach(var inputFileLine in inputFile)
        {
            // Skip empty lines
            if(string.IsNullOrWhiteSpace(inputFileLine))
                continue;

            // Read merged lines
            int pos = 0;
            while(pos < inputFileLine.Length)
            {
                // Parse current control character
                char firstChar = inputFileLine[pos];
                int lineId;
                string lineEndPart = "";
                if(firstChar == 'L')
                {
                    // Line info
                    int separatorIndex = inputFileLine.AsSpan(pos + 2).IndexOf('|');
                    int lId = int.Parse(inputFileLine.AsSpan(pos + 2, separatorIndex));
                    string lContent = inputFileLine[(pos + 2 + separatorIndex + 1)..];

                    compressedLinesLookup.Add(lId, lContent);

                    // The line is fully consumed
                    break;
                }
                else if(firstChar is >= 'a' and <= 's')
                {
                    // Line ID, relative

                    lineId = lastLineId + (firstChar - 'j');
                    lastLineId = lineId;

                    // Is this a prefixed line?
                    ++pos;
                    if(pos < inputFileLine.Length && inputFileLine[pos] == '|')
                    {
                        // Read remaining line part
                        lineEndPart = inputFileLine.Substring(pos + 1);

                        // The line is fully consumed
                        pos = inputFileLine.Length;
                    }
                }
                else if(char.IsDigit(firstChar))
                {
                    // Line ID, absolute

                    int numDigits = 0;
                    for(int i = pos; i < inputFileLine.Length; ++i)
                    {
                        if(!char.IsDigit(inputFileLine[i]))
                            break;
                        ++numDigits;
                    }

                    lineId = int.Parse(inputFileLine.Substring(pos, numDigits));
                    lastLineId = lineId;

                    // Is this a prefixed line?
                    pos += numDigits;
                    if(pos < inputFileLine.Length && inputFileLine[pos] == '|')
                    {
                        // Read remaining line part
                        lineEndPart = inputFileLine.Substring(pos + 1);

                        // The line is fully consumed
                        pos = inputFileLine.Length;
                    }
                }
                else
                    throw new Exception($"{logPrefix} Unexpected control character: '{firstChar}' in line \"{inputFileLine}\"");

                // Extract line
                if(!compressedLinesLookup.TryGetValue(lineId, out string? line))
                    throw new Exception($"{logPrefix} Could not resolve compressed line #{lineId}");

                line += lineEndPart;

                // Parse decompressed line
                string[] parts = line.Split(';');
                string entryType = parts[0];
                switch(entryType)
                {
                    case "c":
                    {
                        // Resolve code locations
                        var source = ResolveLineInfoToImage(parts[1]);
                        var destination = ResolveLineInfoToImage(parts[2]);

                        // Record function name, if it is not already known
                        _functionNameLookup[destination.ImageFileInfo.Id].TryAdd((destination.RelativeStartAddress, destination.RelativeEndAddress), parts[3]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.ImageFileInfo.Id, source.RelativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.ImageFileInfo.Id, destination.RelativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.ImageFileInfo.Id, destination.RelativeEndAddress), null); // For Ret2-only returns (e.g., void functions)

                        // Do not trace branches in prefix mode
                        if(isPrefix)
                            break;

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = source.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = source.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Record call
                        var entry = new Branch
                        {
                            BranchType = Branch.BranchTypes.Call,
                            Taken = true,
                            SourceImageId = source.ImageFileInfo.Id,
                            SourceInstructionRelativeAddress = source.RelativeStartAddress,
                            DestinationImageId = destination.ImageFileInfo.Id,
                            DestinationInstructionRelativeAddress = destination.RelativeStartAddress
                        };
                        entry.Store(traceFileWriter);

                        break;
                    }

                    case "r":
                    {
                        // Resolve code locations
                        var source = ResolveLineInfoToImage(parts[1]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.ImageFileInfo.Id, source.RelativeStartAddress), null);

                        // Do not trace branches in prefix mode
                        if(isPrefix)
                            break;

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = source.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = source.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Remember for next Ret2 entry
                        lastRet1Entry = (source.ImageFileInfo, source.RelativeStartAddress);

                        break;
                    }

                    case "R":
                    {
                        // Resolve code locations
                        var source = ResolveLineInfoToImage(parts[1]);
                        var destination = ResolveLineInfoToImage(parts[2]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.ImageFileInfo.Id, source.RelativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.ImageFileInfo.Id, destination.RelativeStartAddress), null);

                        // Do not trace branches in prefix mode
                        if(isPrefix)
                            break;

                        // Did we see a Ret1 entry? -> more accurate location info
                        Branch entry;
                        if(lastRet1Entry != null)
                        {
                            entry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Return,
                                Taken = true,
                                SourceImageId = lastRet1Entry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastRet1Entry.Value.address,
                                DestinationImageId = destination.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = destination.RelativeStartAddress
                            };

                            lastRet1Entry = null;
                        }
                        else
                        {
                            entry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Return,
                                Taken = true,
                                SourceImageId = source.ImageFileInfo.Id,
                                SourceInstructionRelativeAddress = source.RelativeEndAddress,
                                DestinationImageId = destination.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = destination.RelativeStartAddress
                            };
                        }

                        entry.Store(traceFileWriter);

                        break;
                    }

                    case "C":
                    {
                        // Resolve code locations
                        var location = ResolveLineInfoToImage(parts[1]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.ImageFileInfo.Id, location.RelativeStartAddress), null);

                        // Do not trace branches in prefix mode
                        if(isPrefix)
                            break;

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);
                        }

                        // We have to wait until the next line before we can produce a meaningful branch entry
                        lastCondEntry = (location.ImageFileInfo, location.RelativeStartAddress);
                        lastWasCond = true;

                        break;
                    }

                    case "e":
                    {
                        // Always skip expressions immediately following a conditional, as those simply span the entire conditional statement
                        if(lastWasCond)
                        {
                            lastWasCond = false;
                            break;
                        }

                        // Resolve code locations
                        var location = ResolveLineInfoToImage(parts[1]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.ImageFileInfo.Id, location.RelativeStartAddress), null);

                        // Do not trace branches in prefix mode
                        if(isPrefix)
                            break;

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        break;
                    }

                    case "g":
                    {
                        // Resolve code locations
                        var location = ResolveLineInfoToImage(parts[1]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.ImageFileInfo.Id, location.RelativeStartAddress), null);

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);
                        }

                        // Extract access data
                        int objectId = int.Parse(parts[2]);
                        string offset = parts[3];

                        // Did we already encounter this object?
                        if(!heapObjects.TryGetValue(objectId, out var objectData))
                        {
                            objectData = new HeapObjectData { NextPropertyAddress = 0x100000 };
                            heapObjects.Add(objectId, objectData);

                            var heapAllocation = new HeapAllocation
                            {
                                Id = objectId,
                                Address = nextHeapAllocationAddress,
                                Size = 2 * heapAllocationChunkSize
                            };
                            heapAllocation.Store(traceFileWriter);

                            nextHeapAllocationAddress += 2 * heapAllocationChunkSize;
                        }

                        // Did we already encounter this offset?
                        uint offsetRelativeAddress = objectData.PropertyAddressMapping.GetOrAdd(offset, _ =>
                        {
                            // No, create new entry

                            // Numeric index?
                            if(uint.TryParse(offset, out uint offsetInt))
                                return offsetInt;

                            // Named property
                            return objectData.NextPropertyAddress++;
                        });

                        // Do not memory accesses in prefix mode
                        if(isPrefix)
                            break;

                        // Create memory access
                        var memoryAccess = new HeapMemoryAccess
                        {
                            InstructionImageId = location.ImageFileInfo.Id,
                            InstructionRelativeAddress = location.RelativeStartAddress,
                            HeapAllocationBlockId = objectId,
                            MemoryRelativeAddress = offsetRelativeAddress,
                            Size = 1,
                            IsWrite = false
                        };
                        memoryAccess.Store(traceFileWriter);

                        break;
                    }

                    case "p":
                    {
                        // Resolve code locations
                        var location = ResolveLineInfoToImage(parts[1]);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.ImageFileInfo.Id, location.RelativeStartAddress), null);

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.RelativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);
                        }

                        // Extract access data
                        int objectId = int.Parse(parts[2]);
                        string offset = parts[3];

                        // Did we already encounter this object?
                        if(!heapObjects.TryGetValue(objectId, out var objectData))
                        {
                            objectData = new HeapObjectData { NextPropertyAddress = 0x100000 };
                            heapObjects.Add(objectId, objectData);

                            var heapAllocation = new HeapAllocation
                            {
                                Id = objectId,
                                Address = nextHeapAllocationAddress,
                                Size = 2 * heapAllocationChunkSize
                            };
                            heapAllocation.Store(traceFileWriter);

                            nextHeapAllocationAddress += 2 * heapAllocationChunkSize;
                        }

                        // Did we already encounter this offset?
                        uint offsetRelativeAddress = objectData.PropertyAddressMapping.GetOrAdd(offset, _ =>
                        {
                            // No, create new entry

                            // Numeric index?
                            if(uint.TryParse(offset, out uint offsetInt))
                                return offsetInt;

                            // Named property
                            return objectData.NextPropertyAddress++;
                        });

                        // Do not memory accesses in prefix mode
                        if(isPrefix)
                            break;

                        // Create memory access
                        var memoryAccess = new HeapMemoryAccess
                        {
                            InstructionImageId = location.ImageFileInfo.Id,
                            InstructionRelativeAddress = location.RelativeStartAddress,
                            HeapAllocationBlockId = objectId,
                            MemoryRelativeAddress = offsetRelativeAddress,
                            Size = 1,
                            IsWrite = true
                        };
                        memoryAccess.Store(traceFileWriter);

                        break;
                    }

                    default:
                    {
                        throw new Exception($"{logPrefix} Could not parse line: {line}");
                    }
                }
            }
        }

        if(isPrefix)
        {
            _prefixNextHeapAllocationAddress = nextHeapAllocationAddress;
            _prefixHeapObjects = heapObjects;
            _prefixCompressedLinesLookup = compressedLinesLookup;
        }
    }

    /// <summary>
    /// Resolves a line/column number info into an image and a pair of image-relative start/end addresses. 
    /// </summary>
    /// <param name="lineInfo">
    /// Line number information.
    ///
    /// Supported formats:
    /// - scriptId:startLine:startColumn:endLine:endColumn
    /// - [extern]:functionName:constructor
    /// </param>
    private (TracePrefixFile.ImageFileInfo ImageFileInfo, uint RelativeStartAddress, uint RelativeEndAddress) ResolveLineInfoToImage(string lineInfo)
    {
        string[] parts = lineInfo.Split(':');

        // Try to read existing address data
        var image = _imageFiles[parts[0] == "E" ? _externalFunctionsImageId : int.Parse(parts[0])];
        var imageAddressLookup = _relativeAddressLookup[image.Id];
        var addressData = imageAddressLookup.GetOrAdd(lineInfo, _ =>
        {
            // Address not known yet, compute it

            // Unknown script / external function?
            if(parts[0] == "E")
            {
                // Get address of function, or generate a new one if it does not yet exist
                // Necessary locking is done by the underlying concurrent dictionary
                uint externalFunctionAddress = _externalFunctionAddresses.GetOrAdd(parts[1], _ => Interlocked.Increment(ref _currentExternalFunctionAddress));

                return (externalFunctionAddress, externalFunctionAddress);
            }

            // Normal function
            uint startLine = uint.Parse(parts[1]);
            uint startColumn = uint.Parse(parts[2]);
            uint endLine = uint.Parse(parts[3]);
            uint endColumn = uint.Parse(parts[4]);
            uint startAddress = (startLine << _columnsBits) | startColumn;
            uint endAddress = (endLine << _columnsBits) | endColumn;

            return (startAddress, endAddress);
        });

        return (image, addressData.start, addressData.end);
    }

    protected override Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        string mapDirectoryPath = moduleOptions.GetChildNodeOrDefault("map-directory")?.AsString() ?? throw new ConfigurationException("Missing MAP file directory.");
        _mapDirectory = Directory.CreateDirectory(mapDirectoryPath);

        string? outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString();
        if(outputDirectoryPath != null)
            _outputDirectory = Directory.CreateDirectory(outputDirectoryPath);

        _storeTraces = moduleOptions.GetChildNodeOrDefault("store-traces")?.AsBoolean() ?? false;
        if(_storeTraces && outputDirectoryPath == null)
            throw new ConfigurationException("Missing output directory for preprocessed traces.");

        int? columnsBitsValue = moduleOptions.GetChildNodeOrDefault("columns-bits")?.AsInteger();
        if(columnsBitsValue != null)
            _columnsBits = columnsBitsValue.Value;

        // Sanity check
        if(_columnsBits > 30)
            throw new ConfigurationException("The number of columns bits must not exceed 30, as there must be space left for encoding line numbers.");

        // Compute mask for columns
        _columnMask = (1u << _columnsBits) - 1;

        return Task.CompletedTask;
    }

    public override async Task UnInitAsync()
    {
        List<char> replaceChars = Path.GetInvalidPathChars().Append('/').Append('\\').Append('.').ToList();

        // Save MAP data
        Dictionary<int, List<uint>> requestedMapEntriesPerImage = _requestedMapEntries
            .GroupBy(m => m.Key.imageId)
            .ToDictionary(m => m.Key, m => m
                .Select(n => n.Key.relativeAddress)
                .OrderBy(n => n)
                .ToList()
            );
        Dictionary<int, SortedList<(uint start, uint end), string>> sortedFunctionNameLookup = _functionNameLookup
            .ToDictionary(fnl => fnl.Key, fnl => new SortedList<(uint start, uint end), string>(fnl.Value));
        foreach(var (imageFileId, imageFileInfo) in _imageFiles)
        {
            string mapFileName = Path.Join(_mapDirectory.FullName, replaceChars.Aggregate(imageFileInfo.Name, (current, invalidPathChar) => current.Replace(invalidPathChar, '_')) + ".map");
            await using var mapFileWriter = new StreamWriter(File.Open(mapFileName, FileMode.Create));

            await mapFileWriter.WriteLineAsync(imageFileInfo.Name);

            // Create MAP entries
            foreach(uint relativeAddress in requestedMapEntriesPerImage[imageFileId])
            {
                string name = sortedFunctionNameLookup[imageFileId].LastOrDefault(functionData => functionData.Key.start <= relativeAddress && relativeAddress <= functionData.Key.end, new KeyValuePair<(uint start, uint end), string>((0, 0), "")).Value;

                // Handle [extern] functions separately
                if(imageFileId == _externalFunctionsImage.Id)
                    await mapFileWriter.WriteLineAsync($"{relativeAddress:x8}\t{name}");
                else
                {
                    // Extract column/line data from address
                    int column = (int)(relativeAddress & _columnMask);
                    int line = (int)(relativeAddress >> _columnsBits);

                    await mapFileWriter.WriteLineAsync($"{relativeAddress:x8}\t{name}:{line}:{column}");
                }
            }
        }
    }

    private class HeapObjectData
    {
        public uint NextPropertyAddress { get; set; }

        public ConcurrentDictionary<string, uint> PropertyAddressMapping { get; } = new();
    }
}