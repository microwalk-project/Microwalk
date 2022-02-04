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
    /// Image data of external functions ("[extern]").
    /// </summary>
    private TracePrefixFile.ImageFileInfo _externalFunctionsImage = null!;

    /// <summary>
    /// Metadata about loaded "images" (= scripts), indexed by script file names.
    /// </summary>
    private Dictionary<string, TracePrefixFile.ImageFileInfo> _imageFiles = null!;

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
    /// Maps encoded start and end addresses to function names. Protected by <see cref="_functionNameLookupLock"/>.
    /// </summary>
    private readonly SortedList<(uint start, uint end), string> _functionNameLookup = new();

    /// <summary>
    /// Map file entries, indexed by image ID and relative address.
    /// </summary>
    private readonly Dictionary<int, ConcurrentDictionary<uint, string>> _mapEntries = new();

    /// <summary>
    /// Used for locking the <see cref="_functionNameLookup"/> list.
    /// </summary>
    private readonly object _functionNameLookupLock = new();

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
                // Paths
                string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath) ?? throw new Exception($"Could not determine directory: {traceEntity.RawTraceFilePath}");
                string scriptsFilePath = Path.Combine(rawTraceFileDirectory, "scripts.txt");
                string tracePrefixFilePath = Path.Combine(rawTraceFileDirectory, "prefix.trace");

                // Read scripts data and translate into image file format
                string[] scriptsFileLines = await File.ReadAllLinesAsync(scriptsFilePath);
                int nextImageFileId = 0;
                int maxImageNameLength = 8; // [extern]
                List<TracePrefixFile.ImageFileInfo> imageFiles = new();
                foreach(string line in scriptsFileLines)
                {
                    string[] scriptData = line.Split('\t');
                    var imageFile = new TracePrefixFile.ImageFileInfo
                    {
                        Id = nextImageFileId,
                        Interesting = true,
                        StartAddress = (ulong)nextImageFileId << 32,
                        EndAddress = ((ulong)nextImageFileId << 32) | 0xFFFFFFFFul,
                        Name = scriptData[1]
                    };
                    imageFiles.Add(imageFile);
                    _relativeAddressLookup.Add(imageFile.Id, new ConcurrentDictionary<string, (uint start, uint end)>());
                    _mapEntries.Add(imageFile.Id, new ConcurrentDictionary<uint, string>());

                    if(imageFile.Name.Length > maxImageNameLength)
                        maxImageNameLength = imageFile.Name.Length;

                    ++nextImageFileId;
                }

                // Add dummy image for [extern] functions
                _externalFunctionsImage = new TracePrefixFile.ImageFileInfo
                {
                    Id = nextImageFileId,
                    Interesting = true,
                    StartAddress = (ulong)nextImageFileId << 32,
                    EndAddress = ((ulong)nextImageFileId << 32) | 0xFFFFFFFFul,
                    Name = "[extern]"
                };
                imageFiles.Add(_externalFunctionsImage);
                _relativeAddressLookup.Add(_externalFunctionsImage.Id, new ConcurrentDictionary<string, (uint start, uint end)>());
                _mapEntries.Add(_externalFunctionsImage.Id, new ConcurrentDictionary<uint, string>());

                // Prepare writer for serializing trace data
                using var tracePrefixFileWriter = new FastBinaryWriter(imageFiles.Count * (32 + maxImageNameLength));

                // Write image files
                tracePrefixFileWriter.WriteInt32(imageFiles.Count);
                foreach(var imageFile in imageFiles)
                    imageFile.Store(tracePrefixFileWriter);

                // Load and parse trace prefix data
                _imageFiles = imageFiles.ToDictionary(i => i.Name);
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
        // TODO this is probably way too much
        traceFileWriter.ResizeBuffer(MaxPreprocessedTraceEntrySize * inputFile.Length);

        // Parse trace entries
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastRet1Entry = null;
        (TracePrefixFile.ImageFileInfo imageFileInfo, uint address)? lastCondEntry = null;
        bool lastWasCond = false;
        foreach(var line in inputFile)
        {
            string[] parts = line.Split(';');

            string entryType = parts[0];
            switch(entryType)
            {
                case "Call":
                {
                    // Resolve code locations
                    var source = ResolveLineInfoToImage(parts[1]);
                    var destination = ResolveLineInfoToImage(parts[2]);

                    // Record function name, if it is not already known
                    // TODO Lots of potential for optimization here, if performance becomes a problem
                    lock(_functionNameLookupLock)
                    {
                        if(!_functionNameLookup.ContainsKey((destination.RelativeStartAddress, destination.RelativeEndAddress)))
                            _functionNameLookup.Add((destination.RelativeStartAddress, destination.RelativeEndAddress), parts[3]);
                    }

                    // Produce MAP entries
                    GenerateMapEntry(source.ImageFileInfo.Id, source.RelativeStartAddress);
                    GenerateMapEntry(destination.ImageFileInfo.Id, destination.RelativeStartAddress);
                    GenerateMapEntry(destination.ImageFileInfo.Id, destination.RelativeEndAddress); // For Ret2-only returns (e.g., void functions)

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

                case "Ret1":
                {
                    // Resolve code locations
                    var source = ResolveLineInfoToImage(parts[1]);

                    // Produce MAP entries
                    GenerateMapEntry(source.ImageFileInfo.Id, source.RelativeStartAddress);

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

                case "Ret2":
                {
                    // Resolve code locations
                    var source = ResolveLineInfoToImage(parts[1]);
                    var destination = ResolveLineInfoToImage(parts[2]);

                    // Produce MAP entries
                    GenerateMapEntry(source.ImageFileInfo.Id, source.RelativeStartAddress);
                    GenerateMapEntry(destination.ImageFileInfo.Id, destination.RelativeStartAddress);

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

                case "Cond":
                {
                    // Resolve code locations
                    var location = ResolveLineInfoToImage(parts[1]);

                    // Produce MAP entries
                    GenerateMapEntry(location.ImageFileInfo.Id, location.RelativeStartAddress);

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

                case "Expr":
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
                    GenerateMapEntry(location.ImageFileInfo.Id, location.RelativeStartAddress);

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
            }
        }
    }

    /// <summary>
    /// Resolves a line/column number info into an image and a pair of image-relative start/end addresses. 
    /// </summary>
    /// <param name="lineInfo">
    /// Line number information.
    ///
    /// Supported formats:
    /// - scriptName:startLine:startColumn:endLine:endColumn
    /// - [extern]:functionName:constructor
    /// </param>
    private (TracePrefixFile.ImageFileInfo ImageFileInfo, uint RelativeStartAddress, uint RelativeEndAddress) ResolveLineInfoToImage(string lineInfo)
    {
        string[] parts = lineInfo.Split(':');

        // Try to read existing address data
        var image = _imageFiles[parts[0]];
        var imageAddressLookup = _relativeAddressLookup[image.Id];
        var addressData = imageAddressLookup.GetOrAdd(lineInfo, _ =>
        {
            // Address not known yet, compute it

            // Unknown script / external function?
            if(parts[0] == "[extern]")
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

    /// <summary>
    /// Generates a MAP file entry for the given code fragment.
    /// </summary>
    /// <param name="imageId">Image ID.</param>
    /// <param name="relativeAddress">Encoded address of the code fragment</param>
    private void GenerateMapEntry(int imageId, uint relativeAddress)
    {
        // Generate entry, if it does not yet exist
        var imageMapEntries = _mapEntries[imageId];
        imageMapEntries.GetOrAdd(relativeAddress, _ =>
        {
            // Find enclosing function
            string enclosingName;
            lock(_functionNameLookupLock)
            {
                // Find maximum start address which still includes the given address, to avoid accidentally matching a parent function
                // The collection is sorted in ascending order. Start == End is possible (some external functions do not have line numbers).
                // TODO This is not very efficient
                enclosingName = _functionNameLookup.LastOrDefault(functionData => functionData.Key.start <= relativeAddress && relativeAddress <= functionData.Key.end, new KeyValuePair<(uint start, uint end), string>((0, 0), "")).Value;
            }

            // Handle [extern] function separately
            if(imageId == _externalFunctionsImage.Id)
                return enclosingName;

            // Extract column/line data from address
            int column = (int)(relativeAddress & _columnMask);
            int line = (int)(relativeAddress >> _columnsBits);

            return $"{enclosingName}:{line}:{column}";
        });
    }

    protected override Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        string mapDirectoryPath = moduleOptions.GetChildNodeOrDefault("map-directory")?.AsString() ?? throw new ConfigurationException("Missing MAP file directory.");
        _mapDirectory = new DirectoryInfo(mapDirectoryPath);
        if(!_mapDirectory.Exists)
            _mapDirectory.Create();

        string? outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString();
        if(outputDirectoryPath != null)
        {
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();
        }

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
        // Save MAP data
        foreach(var (imageFileName, imageFileInfo) in _imageFiles)
        {
            string mapFileName = Path.Join(_mapDirectory.FullName, Path.GetInvalidPathChars().Aggregate(imageFileName, (current, invalidPathChar) => current.Replace(invalidPathChar, '_')) + ".map");
            await using var mapFileWriter = new StreamWriter(File.Open(mapFileName, FileMode.Create));

            await mapFileWriter.WriteLineAsync(imageFileName);

            var mapEntries = _mapEntries[imageFileInfo.Id];
            foreach(var addressData in mapEntries.OrderBy(e => e.Key))
            {
                await mapFileWriter.WriteLineAsync($"{addressData.Key:x8}\t{addressData.Value}");
            }
        }
    }
}