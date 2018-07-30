using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Allows dumping a trace file.
    /// </summary>
    internal class TraceDump
    {
        /// <summary>
        /// Contains symbol information of image files.
        /// </summary>
        private Dictionary<string, MapFile> _mapFiles = new Dictionary<string, MapFile>();

        /// <summary>
        /// Maps image IDs to their map file names.
        /// </summary>
        private Dictionary<int, string> _imageIdMapFileNameMapping = new Dictionary<int, string>();

        /// <summary>
        /// Regex for removing extensions from image names
        /// </summary>
        private static readonly Regex _imageNameRegex = new Regex("\\.(dll|exe)(\\..*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);


        /// <summary>
        /// Creates a new execution trace generator.
        /// </summary>
        /// <param name="mapFileNames">A list of linker MAP files containing function information.</param>
        public TraceDump(IEnumerable<string> mapFileNames)
        {
            // Load map files
            foreach(string mapFileName in mapFileNames)
                _mapFiles.Add(_imageNameRegex.Replace(Path.GetFileNameWithoutExtension(mapFileName).ToLower(), ""), new MapFile(mapFileName));
        }

        /// <summary>
        /// Writes the execution data of the given trace file into the given output file.
        /// </summary>
        /// <param name="traceFile">The trace file to be converted.</param>
        /// <param name="outputFileName">The output file name.</param>
        /// <param name="callChainInstructionAddress">The address of the instruction for which a call chain shall be printed (else 0).</param>
        /// <param name="relativeMemoryAddresses">Determines whether allocation block accesses should be converted to their relative offsets.</param>
        public void Dump(TraceFile traceFile, string outputFileName, uint callChainInstructionAddress, bool relativeMemoryAddresses)
        {
            // Make sure that all entries are loaded (the trace file is iterated two times simultaneously)
            traceFile.CacheEntries();

            // Print call chain?
            if(callChainInstructionAddress != 0)
                Program.Log($"Call chain instruction address: 0x{callChainInstructionAddress.ToString("X8")}\n", Program.LogLevel.Debug);

            // Open output file for writing
            TextWriter outputWriter = null;
            using(!string.IsNullOrWhiteSpace(outputFileName) ? outputWriter = new StreamWriter(File.Open(outputFileName, FileMode.Create)) : outputWriter = Console.Out)
            {
                // Run through entries
                Stack<string> callStack = new Stack<string>();
                int callLevel = 0;
                int entryIndexWidth = (int)Math.Ceiling(Math.Log10(traceFile.EntryCount));
                int i = 0;
                var traceFileEntryEncoder = TraceFileDiff.EncodeTraceEntries(traceFile).GetEnumerator();
                foreach(TraceEntry entry in traceFile.Entries)
                {
                    // Get encoding for current entry
                    traceFileEntryEncoder.MoveNext();
                    long encodedEntry = traceFileEntryEncoder.Current;

                    // Print entry index
                    outputWriter.Write($"[{i.ToString().PadLeft(entryIndexWidth, ' ')}] \t");

                    // Add indentation depending on call level
                    outputWriter.Write(new string('\t', callLevel));

                    // Print entry depending on type
                    switch(entry.EntryType)
                    {
                        case TraceEntryTypes.Allocation:
                        {
                            // Print entry
                            AllocationEntry allocationEntry = (AllocationEntry)entry;
                            outputWriter.WriteLine($"Alloc {allocationEntry.Address.ToString("X16")} :: {(allocationEntry.Address + allocationEntry.Size).ToString("X16")}, {allocationEntry.Size} bytes");

                            break;
                        }

                        case TraceEntryTypes.Free:
                        {
                            // Print entry
                            FreeEntry freeEntry = (FreeEntry)entry;
                            outputWriter.WriteLine($"Free {freeEntry.Address}");

                            break;
                        }

                        case TraceEntryTypes.AllocMemoryRead:
                        case TraceEntryTypes.AllocMemoryWrite:
                        case TraceEntryTypes.ImageMemoryRead:
                        case TraceEntryTypes.ImageMemoryWrite:
                        {
                            uint instructionAddr = 0;
                            switch(entry.EntryType)
                            {
                                case TraceEntryTypes.AllocMemoryRead:
                                {
                                    // Retrieve function name of executed instruction
                                    AllocMemoryReadEntry allocMemoryReadEntry = (AllocMemoryReadEntry)entry;
                                    instructionAddr = allocMemoryReadEntry.InstructionAddress;
                                    string formattedInstructionAddress = FormatAddressWithSymbolNames(allocMemoryReadEntry.InstructionAddress, allocMemoryReadEntry.ImageId, allocMemoryReadEntry.ImageName);

                                    // Get allocation block relative offset from encoded entry
                                    uint addressPart = (uint)(encodedEntry >> 32);
                                    string formattedMemoryAddress = allocMemoryReadEntry.MemoryAddress.ToString("X16");
                                    if(relativeMemoryAddresses && addressPart != 0)
                                        formattedMemoryAddress = $"rel:{addressPart.ToString("X")}";

                                    // Print entry
                                    outputWriter.WriteLine($"MemRead <0x{allocMemoryReadEntry.InstructionAddress:X8} {formattedInstructionAddress}>  {formattedMemoryAddress}");

                                    break;
                                }

                                case TraceEntryTypes.AllocMemoryWrite:
                                {
                                    // Retrieve function name of executed instruction
                                    AllocMemoryWriteEntry allocMemoryWriteEntry = (AllocMemoryWriteEntry)entry;
                                    instructionAddr = allocMemoryWriteEntry.InstructionAddress;
                                    string formattedInstructionAddress = FormatAddressWithSymbolNames(allocMemoryWriteEntry.InstructionAddress, allocMemoryWriteEntry.ImageId, allocMemoryWriteEntry.ImageName);

                                    // Get allocation block relative offset from encoded entry
                                    uint addressPart = (uint)(encodedEntry >> 32);
                                    string formattedMemoryAddress = allocMemoryWriteEntry.MemoryAddress.ToString("X16");
                                    if(relativeMemoryAddresses && addressPart != 0)
                                        formattedMemoryAddress = $"rel:{addressPart.ToString("X")}";

                                    // Print entry
                                    outputWriter.WriteLine($"MemWrite <0x{allocMemoryWriteEntry.InstructionAddress:X8} {formattedInstructionAddress}>  {formattedMemoryAddress}");

                                    break;
                                }

                                case TraceEntryTypes.ImageMemoryRead:
                                {
                                    // Retrieve function name of executed instruction
                                    ImageMemoryReadEntry imageMemoryReadEntry = (ImageMemoryReadEntry)entry;
                                    instructionAddr = imageMemoryReadEntry.InstructionAddress;
                                    string formattedInstructionAddress = FormatAddressWithSymbolNames(imageMemoryReadEntry.InstructionAddress, imageMemoryReadEntry.InstructionImageId, imageMemoryReadEntry.InstructionImageName);

                                    // Print entry
                                    outputWriter.WriteLine($"ImgRead <0x{imageMemoryReadEntry.InstructionAddress:X8} {formattedInstructionAddress}>  {imageMemoryReadEntry.MemoryImageName}+{imageMemoryReadEntry.MemoryAddress.ToString("X8")}");

                                    break;
                                }

                                case TraceEntryTypes.ImageMemoryWrite:
                                {
                                    // Retrieve function name of executed instruction
                                    ImageMemoryWriteEntry imageMemoryWriteEntry = (ImageMemoryWriteEntry)entry;
                                    instructionAddr = imageMemoryWriteEntry.InstructionAddress;
                                    string formattedInstructionAddress = FormatAddressWithSymbolNames(imageMemoryWriteEntry.InstructionAddress, imageMemoryWriteEntry.InstructionImageId, imageMemoryWriteEntry.InstructionImageName);

                                    // Print entry
                                    outputWriter.WriteLine($"ImgWrite <0x{imageMemoryWriteEntry.InstructionAddress:X8} {formattedInstructionAddress}>  {imageMemoryWriteEntry.MemoryImageName}+{imageMemoryWriteEntry.MemoryAddress.ToString("X8")}");

                                    break;
                                }
                            }

                            // Output call chain of this instruction?
                            if(instructionAddr == callChainInstructionAddress)
                            {
                                outputWriter.WriteLine($"[{new string('-', entryIndexWidth)}] CALL CHAIN:");
                                foreach(string call in callStack.Reverse())
                                {
                                    outputWriter.Write($"{new string(' ', 1 + entryIndexWidth + 1 + 1 + 4)}");
                                    outputWriter.WriteLine(call);
                                }
                            }
                            break;
                        }

                        case TraceEntryTypes.Branch:
                        {
                            // Retrieve function names of instructions
                            BranchEntry branchEntry = (BranchEntry)entry;
                            string formattedSourceInstructionAddress = FormatAddressWithSymbolNames(branchEntry.SourceInstructionAddress, branchEntry.SourceImageId, branchEntry.SourceImageName);
                            string formattedDestinationInstructionAddress = FormatAddressWithSymbolNames(branchEntry.DestinationInstructionAddress, branchEntry.DestinationImageId, branchEntry.DestinationImageName);

                            // Output entry and update call level
                            if(branchEntry.BranchType == BranchTypes.Call)
                            {
                                string lg = $"Call <0x{branchEntry.SourceInstructionAddress:X8} {formattedSourceInstructionAddress}>    ->    <0x{branchEntry.DestinationInstructionAddress:X8} {formattedDestinationInstructionAddress}>";
                                outputWriter.WriteLine(lg);
                                callStack.Push(lg);
                                ++callLevel;
                            }
                            else if(branchEntry.BranchType == BranchTypes.Ret)
                            {
                                outputWriter.WriteLine($"Return <0x{branchEntry.SourceInstructionAddress:X8} {formattedSourceInstructionAddress}>    ->    <0x{branchEntry.DestinationInstructionAddress:X8} {formattedDestinationInstructionAddress}>");
                                if(callStack.Count() > 0)
                                    callStack.Pop();
                                --callLevel;

                                // Check indentation
                                if(callLevel < 0)
                                {
                                    // Just output a warning, this was probably caused by trampoline functions and similar constructions
                                    callLevel = 0;
                                    Program.Log($"Warning: Indentation error in line {i}\n", Program.LogLevel.Warning);
                                }
                            }
                            else if(branchEntry.BranchType == BranchTypes.Jump)
                                outputWriter.WriteLine($"Jump <0x{branchEntry.SourceInstructionAddress:X8} {formattedSourceInstructionAddress}>    ->    <0x{branchEntry.DestinationInstructionAddress:X8} {formattedDestinationInstructionAddress}> {(branchEntry.Taken ? "" : "not ")}taken");

                            break;
                        }
                    }

                    // Next entry
                    ++i;
                }
            }
        }

        /// <summary>
        /// Tries to retrieve the symbol for the given image offset and formats it.
        /// </summary>
        /// <param name="address">The image offset.</param>
        /// <param name="imageId">The ID of the image.</param>
        /// <param name="imageName">The file name of the image.</param>
        /// <returns></returns>
        private string FormatAddressWithSymbolNames(uint address, int imageId, string imageName)
        {
            // Is the map file provided?
            if(_mapFiles.Any())
            {

                // Is there a map file for this image?
                string fmt = "";
                string cleanedImageName = _imageNameRegex.Replace(imageName, "").ToLower();
                if(!_imageIdMapFileNameMapping.ContainsKey(imageId))
                {
                    // Check whether map file exists
                    if(_mapFiles.ContainsKey(cleanedImageName))
                        _imageIdMapFileNameMapping.Add(imageId, cleanedImageName);
                    else
                        _imageIdMapFileNameMapping.Add(imageId, null);
                }
                string mapFileName = _imageIdMapFileNameMapping[imageId];

                // Map file available?
                if(mapFileName == null)
                {
                    // Just output plain image offset
                    fmt = $"{imageName}:{address.ToString("X8")}";
                }
                else
                {
                    // Format symbol name and relative offset
                    ulong cleanedAddress = address - 0x1000; // TODO hardcoded -> correct?
                    (ulong, string) symbolData = _mapFiles[mapFileName].GetSymbolNameByAddress(cleanedAddress);
                    fmt = $"{imageName}:{symbolData.Item2}+{(cleanedAddress - symbolData.Item1).ToString("X")}";
                }
                return fmt;
            }
            else
            {
                return "";
            }
        }
    }
}
