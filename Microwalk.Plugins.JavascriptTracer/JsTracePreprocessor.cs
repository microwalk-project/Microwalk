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
    /// Metadata and lookup objects for loaded "images" (= scripts), indexed by script IDs.
    /// </summary>
    private readonly Dictionary<int, ImageData> _imageData = new();

    /// <summary>
    /// The next address which is assigned to an external function.
    /// </summary>
    private uint _currentExternalFunctionAddress = 1;

    /// <summary>
    /// Lookup for addresses assigned to external functions "[extern]:functionName", indexed by functionName.
    /// </summary>
    private readonly ConcurrentDictionary<string, uint> _externalFunctionAddresses = new();

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
                    _imageData.Add(imageFileId, new ImageData(imageFile));

                    if(imageFile.Name.Length > maxImageNameLength)
                        maxImageNameLength = imageFile.Name.Length;
                }

                // Add dummy image for [extern] functions
                _externalFunctionsImageId = _imageData.Max(i => i.Key) + 1;
                _externalFunctionsImage = new TracePrefixFile.ImageFileInfo
                {
                    Id = _externalFunctionsImageId,
                    Interesting = true,
                    StartAddress = (ulong)_externalFunctionsImageId << 32,
                    EndAddress = ((ulong)_externalFunctionsImageId << 32) | 0xFFFFFFFFul,
                    Name = "[extern]"
                };
                _imageData.Add(_externalFunctionsImageId, new ImageData(_externalFunctionsImage));

                // Prepare writer for serializing trace data
                // We initialize it with some robust initial capacity, to reduce amount of copying while keeping memory overhead low
                using var tracePrefixFileWriter = new FastBinaryBufferWriter(_imageData.Count * (32 + maxImageNameLength) + 1 * 1024 * 1024);

                // Write image files
                tracePrefixFileWriter.WriteInt32(_imageData.Count);
                foreach(var imageData in _imageData)
                    imageData.Value.ImageFileInfo.Store(tracePrefixFileWriter);

                // Load and parse trace prefix data
                PreprocessFile(tracePrefixFilePath, true, tracePrefixFileWriter, "[preprocess:prefix]");

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
        catch
        {
            // Ensure that other threads don't try to process the prefix as well
            _firstTestcase = false;

            throw;
        }
        finally
        {
            _firstTestcaseSemaphore.Release();
        }

        // Preprocess trace data
        await Logger.LogDebugAsync($"[preprocess:{traceEntity.Id}] Preprocessing trace");
        if(_storeTraces)
        {
            // Write trace to file, do not keep it in memory
            string preprocessedTraceFilePath = Path.Combine(_outputDirectory!.FullName, Path.GetFileName(traceEntity.RawTraceFilePath) + ".preprocessed");
            using var traceFileWriter = new FastBinaryFileWriter(preprocessedTraceFilePath);
            PreprocessFile(traceEntity.RawTraceFilePath, false, traceFileWriter, $"[preprocess:{traceEntity.Id}]");
            traceFileWriter.Flush();

            // Create trace file object
            traceEntity.PreprocessedTraceFile = new TraceFile(_tracePrefix, preprocessedTraceFilePath);
        }
        else
        {
            // Keep trace in memory for immediate analysis
            using var traceFileWriter = new FastBinaryBufferWriter(1 * 1024 * 1024);
            PreprocessFile(traceEntity.RawTraceFilePath, false, traceFileWriter, $"[preprocess:{traceEntity.Id}]");

            // Create trace file object
            var preprocessedTraceData = traceFileWriter.Buffer.AsMemory(0, traceFileWriter.Length);
            traceEntity.PreprocessedTraceFile = new TraceFile(_tracePrefix, preprocessedTraceData);
        }
    }

    private void PreprocessFile(string inputFileName, bool isPrefix, IFastBinaryWriter traceFileWriter, string logPrefix)
    {
        // Read entire raw trace into memory, for faster processing
        using var inputFileStream = File.Open(inputFileName, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Options = FileOptions.SequentialScan,
            BufferSize = 1 * 1024 * 1024
        });
        using var inputFileStreamReader = new StreamReader(inputFileStream, Encoding.UTF8);

        // If we are writing to memory, set the capacity of the writer to a rough estimate of the preprocessed file size,
        // in order to avoid reallocations and expensive copying
        if(traceFileWriter is FastBinaryBufferWriter binaryBufferWriter)
            binaryBufferWriter.ResizeBuffer((int)inputFileStream.Length);

        // Parse trace entries
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastRet1Entry = null;
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastCondEntry = null;
        Dictionary<int, HeapObjectData> heapObjects = _prefixHeapObjects == null ? new() : new(_prefixHeapObjects);
        Dictionary<int, string> compressedLinesLookup = _prefixCompressedLinesLookup == null ? new() : new(_prefixCompressedLinesLookup);
        ulong nextHeapAllocationAddress = _prefixNextHeapAllocationAddress;
        const uint heapAllocationChunkSize = 0x100000;
        int lastLineId = 0;
        int inputBufferLength = 0;
        int inputBufferPosition = 0;
        char[] inputBuffer = new char[1 * 1024 * 1024];
        char[] lineBuffer = new char[1024]; // For storing a single, decompressed line
        while(true)
        {
            // Empty buffer? -> Read next chunk
            if(inputBufferPosition == inputBufferLength)
            {
                inputBufferLength = inputFileStreamReader.ReadBlock(inputBuffer);
                if(inputBufferLength == 0)
                    break;

                inputBufferPosition = 0;
            }

            // Find end of next line in input buffer
            int lineEnd = inputBufferPosition;
            Span<char> currentInputFileLineSpan = Span<char>.Empty;
            bool foundNewLine = false;
            while(lineEnd < inputBufferLength)
            {
                if(inputBuffer[lineEnd] == '\n')
                {
                    currentInputFileLineSpan = inputBuffer.AsSpan(inputBufferPosition..lineEnd);
                    foundNewLine = true;
                    break;
                }

                ++lineEnd;
            }

            // If we could not find the line end in the buffer, we need to read more data
            if(!foundNewLine)
            {
                // Copy beginning of line to buffer begin
                for(int i = inputBufferPosition; i < inputBufferLength; ++i)
                    inputBuffer[i - inputBufferPosition] = inputBuffer[i];
                inputBufferLength -= inputBufferPosition;
                inputBufferPosition = 0;

                // Append the new data
                int dataRead = inputFileStreamReader.ReadBlock(inputBuffer.AsSpan(inputBufferLength..));
                inputBufferLength += dataRead;
                if(dataRead == 0)
                {
                    // No data retrieved, either the buffer is entirely full or the file has ended
                    // Since the buffer is _very_ large, we just assume the latter, and fail otherwise
                    if(inputFileStream.Position < inputFileStream.Length)
                        throw new Exception("The file read buffer is too small (no data returned).");

                    currentInputFileLineSpan = inputBuffer.AsSpan(..inputBufferLength);
                }
                else
                {
                    // Look for newline or buffer end
                    lineEnd = inputBufferPosition;
                    while(lineEnd < inputBufferLength)
                    {
                        if(inputBuffer[lineEnd] == '\n')
                        {
                            currentInputFileLineSpan = inputBuffer.AsSpan(inputBufferPosition..lineEnd);
                            foundNewLine = true;
                            break;
                        }

                        ++lineEnd;
                    }

                    if(!foundNewLine)
                    {
                        if(inputFileStream.Position < inputFileStream.Length)
                            throw new Exception("The file read buffer is too small (could not find line end).");

                        currentInputFileLineSpan = inputBuffer.AsSpan(inputBufferPosition..lineEnd);
                    }
                }
            }

            // Update current input buffer position
            // The line span automatically skips the \n, as the end of the range is exclusive
            inputBufferPosition = lineEnd + 1;

            // Skip empty lines
            if(currentInputFileLineSpan.Length == 0)
                continue;

            // Read merged lines
            int pos = 0;
            while(pos < currentInputFileLineSpan.Length)
            {
                // Parse current control character
                char firstChar = currentInputFileLineSpan[pos];
                int lineId;
                ReadOnlySpan<char> lineEndPart = ReadOnlySpan<char>.Empty;
                if(firstChar == 'L')
                {
                    // Line info
                    int separatorIndex = currentInputFileLineSpan.Slice(pos + 2).IndexOf('|');
                    int lId = int.Parse(currentInputFileLineSpan.Slice(pos + 2, separatorIndex));
                    string lContent = new string(currentInputFileLineSpan.Slice(pos + 2 + separatorIndex + 1));

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
                    if(pos < currentInputFileLineSpan.Length && currentInputFileLineSpan[pos] == '|')
                    {
                        // Read remaining line part
                        lineEndPart = currentInputFileLineSpan.Slice(pos + 1);

                        // The line is fully consumed
                        pos = currentInputFileLineSpan.Length;
                    }
                }
                else if(char.IsDigit(firstChar))
                {
                    // Line ID, absolute

                    int numDigits = 0;
                    for(int i = pos; i < currentInputFileLineSpan.Length; ++i)
                    {
                        if(!char.IsDigit(currentInputFileLineSpan[i]))
                            break;
                        ++numDigits;
                    }

                    lineId = int.Parse(currentInputFileLineSpan.Slice(pos, numDigits));
                    lastLineId = lineId;

                    // Is this a prefixed line?
                    pos += numDigits;
                    if(pos < currentInputFileLineSpan.Length && currentInputFileLineSpan[pos] == '|')
                    {
                        // Read remaining line part
                        lineEndPart = currentInputFileLineSpan.Slice(pos + 1);

                        // The line is fully consumed
                        pos = currentInputFileLineSpan.Length;
                    }
                }
                else
                    throw new Exception($"{logPrefix} Unexpected control character: '{firstChar}' in line \"{new string(currentInputFileLineSpan)}\"");

                // Extract line
                if(!compressedLinesLookup.TryGetValue(lineId, out string? decompressedLine))
                    throw new Exception($"{logPrefix} Could not resolve compressed line #{lineId}");

                // Compose final decompressed line
                decompressedLine.CopyTo(lineBuffer);
                lineEndPart.CopyTo(lineBuffer.AsSpan(decompressedLine.Length));
                ReadOnlySpan<char> line = lineBuffer.AsSpan(0, decompressedLine.Length + lineEndPart.Length);

                // Parse decompressed line
                const char separator = ';';
                var lineParts = line;
                char entryType = NextSplit(ref lineParts, separator)[0];
                switch(entryType)
                {
                    case 'c':
                    {
                        // Parse line
                        var sourcePart = NextSplit(ref lineParts, separator);
                        var destinationPart = NextSplit(ref lineParts, separator);
                        var namePart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var source = ResolveLineInfoToImage(sourcePart);
                        var destination = ResolveLineInfoToImage(destinationPart);

                        // Record function name, if it is not already known
                        destination.imageData.FunctionNameLookup.TryAdd((destination.relativeStartAddress, destination.relativeEndAddress), new string(namePart));

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.imageData.ImageFileInfo.Id, source.relativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.imageData.ImageFileInfo.Id, destination.relativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.imageData.ImageFileInfo.Id, destination.relativeEndAddress), null); // For Ret2-only returns (e.g., void functions)

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
                                DestinationImageId = source.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = source.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Record call
                        var entry = new Branch
                        {
                            BranchType = Branch.BranchTypes.Call,
                            Taken = true,
                            SourceImageId = source.imageData.ImageFileInfo.Id,
                            SourceInstructionRelativeAddress = source.relativeStartAddress,
                            DestinationImageId = destination.imageData.ImageFileInfo.Id,
                            DestinationInstructionRelativeAddress = destination.relativeStartAddress
                        };
                        entry.Store(traceFileWriter);

                        break;
                    }

                    case 'r':
                    {
                        // Parse line
                        var sourcePart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var source = ResolveLineInfoToImage(sourcePart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.imageData.ImageFileInfo.Id, source.relativeStartAddress), null);

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
                                DestinationImageId = source.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = source.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Remember for next Ret2 entry
                        lastRet1Entry = (source.imageData.ImageFileInfo, source.relativeStartAddress);

                        break;
                    }

                    case 'R':
                    {
                        // Parse line
                        var sourcePart = NextSplit(ref lineParts, separator);
                        var destinationPart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var source = ResolveLineInfoToImage(sourcePart);
                        var destination = ResolveLineInfoToImage(destinationPart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((source.imageData.ImageFileInfo.Id, source.relativeStartAddress), null);
                        _requestedMapEntries.TryAdd((destination.imageData.ImageFileInfo.Id, destination.relativeStartAddress), null);

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
                                DestinationImageId = destination.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = destination.relativeStartAddress
                            };

                            lastRet1Entry = null;
                        }
                        else
                        {
                            entry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Return,
                                Taken = true,
                                SourceImageId = source.imageData.ImageFileInfo.Id,
                                SourceInstructionRelativeAddress = source.relativeEndAddress,
                                DestinationImageId = destination.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = destination.relativeStartAddress
                            };
                        }

                        entry.Store(traceFileWriter);

                        break;
                    }

                    case 'C':
                    {
                        // Parse line
                        var locationPart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var location = ResolveLineInfoToImage(locationPart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.imageData.ImageFileInfo.Id, location.relativeStartAddress), null);

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
                                DestinationImageId = location.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);
                        }

                        // We have to wait until the next line before we can produce a meaningful branch entry
                        lastCondEntry = (location.imageData.ImageFileInfo, location.relativeStartAddress);

                        break;
                    }

                    case 'e':
                    {
                        // Parse line
                        var locationPart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var location = ResolveLineInfoToImage(locationPart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.imageData.ImageFileInfo.Id, location.relativeStartAddress), null);

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
                                DestinationImageId = location.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        break;
                    }

                    case 'g':
                    {
                        // Parse line
                        var locationPart = NextSplit(ref lineParts, separator);
                        var objectIdPart = NextSplit(ref lineParts, separator);
                        var offsetPart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var location = ResolveLineInfoToImage(locationPart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.imageData.ImageFileInfo.Id, location.relativeStartAddress), null);

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Extract access data
                        int objectId = int.Parse(objectIdPart);
                        string offset = new string(offsetPart);

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
                            InstructionImageId = location.imageData.ImageFileInfo.Id,
                            InstructionRelativeAddress = location.relativeStartAddress,
                            HeapAllocationBlockId = objectId,
                            MemoryRelativeAddress = offsetRelativeAddress,
                            Size = 1,
                            IsWrite = false
                        };
                        memoryAccess.Store(traceFileWriter);

                        break;
                    }

                    case 'p':
                    {
                        // Parse line
                        var locationPart = NextSplit(ref lineParts, separator);
                        var objectIdPart = NextSplit(ref lineParts, separator);
                        var offsetPart = NextSplit(ref lineParts, separator);

                        // Resolve code locations
                        var location = ResolveLineInfoToImage(locationPart);

                        // Produce MAP entries
                        _requestedMapEntries.TryAdd((location.imageData.ImageFileInfo.Id, location.relativeStartAddress), null);

                        // Create branch entry, if there is a pending conditional
                        if(lastCondEntry != null)
                        {
                            var branchEntry = new Branch
                            {
                                BranchType = Branch.BranchTypes.Jump,
                                Taken = true,
                                SourceImageId = lastCondEntry.Value.imageFileInfo.Id,
                                SourceInstructionRelativeAddress = lastCondEntry.Value.address,
                                DestinationImageId = location.imageData.ImageFileInfo.Id,
                                DestinationInstructionRelativeAddress = location.relativeStartAddress
                            };
                            branchEntry.Store(traceFileWriter);

                            lastCondEntry = null;
                        }

                        // Extract access data
                        int objectId = int.Parse(objectIdPart);
                        string offset = new string(offsetPart);

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
                            InstructionImageId = location.imageData.ImageFileInfo.Id,
                            InstructionRelativeAddress = location.relativeStartAddress,
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
    private (ImageData imageData, uint relativeStartAddress, uint relativeEndAddress) ResolveLineInfoToImage(ReadOnlySpan<char> lineInfo)
    {
        const char separator = ':';

        // We use line info as key for caching known addresses
        string lineInfoString = new string(lineInfo);

        var part0 = NextSplit(ref lineInfo, separator);

        // Try to read existing address data
        bool isExternal = part0[0] == 'E';
        var imageData = _imageData[isExternal ? _externalFunctionsImageId : int.Parse(part0)];
        var addressData = imageData.RelativeAddressLookup.GetOrAdd(lineInfoString, _ =>
        {
            // Address not known yet, compute it

            // Split
            var lineInfoStringSpan = _.AsSpan(); // We can't capture the lineInfo Span directly
            NextSplit(ref lineInfoStringSpan, separator); // part0 is already handled
            var part1 = NextSplit(ref lineInfoStringSpan, separator);

            // Unknown script / external function?
            if(isExternal)
            {
                // Get address of function, or generate a new one if it does not yet exist
                // Necessary locking is done by the underlying concurrent dictionary
                uint externalFunctionAddress = _externalFunctionAddresses.GetOrAdd(new string(part1), _ => Interlocked.Increment(ref _currentExternalFunctionAddress));

                return (externalFunctionAddress, externalFunctionAddress);
            }

            // Split
            var part2 = NextSplit(ref lineInfoStringSpan, separator);
            var part3 = NextSplit(ref lineInfoStringSpan, separator);
            var part4 = NextSplit(ref lineInfoStringSpan, separator);

            // Normal function
            uint startLine = uint.Parse(part1);
            uint startColumn = uint.Parse(part2);
            uint endLine = uint.Parse(part3);
            uint endColumn = uint.Parse(part4);
            uint startAddress = (startLine << _columnsBits) | startColumn;
            uint endAddress = (endLine << _columnsBits) | endColumn;

            return (startAddress, endAddress);
        });

        return (imageData, addressData.start, addressData.end);
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
        Dictionary<int, SortedList<(uint start, uint end), string>> sortedFunctionNameLookup = _imageData
            .ToDictionary(i => i.Key, i => new SortedList<(uint start, uint end), string>(i.Value.FunctionNameLookup));
        foreach(var (imageFileId, imageData) in _imageData)
        {
            string mapFileName = Path.Join(_mapDirectory.FullName, replaceChars.Aggregate(imageData.ImageFileInfo.Name, (current, invalidPathChar) => current.Replace(invalidPathChar, '_')) + ".map");
            await using var mapFileWriter = new StreamWriter(File.Open(mapFileName, FileMode.Create));

            await mapFileWriter.WriteLineAsync(imageData.ImageFileInfo.Name);

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

    /// <summary>
    /// Looks for the separator and returns the next string part before it.
    /// Updates the given string span to point to the remaining string.
    /// </summary>
    /// <param name="str">String to split.</param>
    /// <param name="separator">Split character.</param>
    /// <returns></returns>
    private ReadOnlySpan<char> NextSplit(ref ReadOnlySpan<char> str, char separator)
    {
        // Look for separator
        for(int i = 0; i < str.Length; ++i)
        {
            if(str[i] == separator)
            {
                // Get part
                var part = str[..i];
                str = str[(i + 1)..];
                return part;
            }
        }

        // Not found, return entire remaining string
        var tmp = str;
        str = ReadOnlySpan<char>.Empty;
        return tmp;
    }

    private class HeapObjectData
    {
        public uint NextPropertyAddress { get; set; }

        public ConcurrentDictionary<string, uint> PropertyAddressMapping { get; } = new();
    }

    private class ImageData
    {
        public ImageData(TracePrefixFile.ImageFileInfo imageFileInfo)
        {
            ImageFileInfo = imageFileInfo;
        }

        /// <summary>
        /// Image file info.
        /// </summary>
        public TracePrefixFile.ImageFileInfo ImageFileInfo { get; }

        /// <summary>
        /// Lookup for all encoded relative addresses. Indexed by encoding ("script.js:1:2:1:3").
        /// </summary>
        public ConcurrentDictionary<string, (uint start, uint end)> RelativeAddressLookup { get; } = new();

        /// <summary>
        /// Maps encoded start and end addresses to function names.
        /// </summary>
        public ConcurrentDictionary<(uint start, uint end), string> FunctionNameLookup { get; } = new();
    }
}