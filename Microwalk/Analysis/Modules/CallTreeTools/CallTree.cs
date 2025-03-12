using System.Collections.Generic;

namespace Microwalk.Analysis.Modules.CallTreeTools;

public class CallTree
{
    /// <summary>
    /// Call stack ID of the root node (base value for call stack ID hashing)
    /// </summary>
    public const ulong RootNodeCallStackId = 0;
    
    /// <summary>
    /// Indicates that an encoded address corresponds to an image offset.
    /// </summary>
    public const ulong AddressIdFlagImage = 0x0000_0000_0000_0000;
    
    /// <summary>
    /// Indicates that an encoded address corresponds to a memory access.
    /// This flag is not set if the address is only executed (i.e., contains an instruction).
    /// </summary>
    public const ulong AddressIdFlagMemory = 0x8000_0000_0000_0000;
    
    /// <summary>
    /// Indicates that an encoded address corresponds to a heap memory access.
    /// </summary>
    public const ulong AddressIdFlagHeap = 0x4000_0000_0000_0000;
    
    /// <summary>
    /// Mask for extracting the encoded address flags.
    /// </summary>
    public const ulong AddressIdFlagsMask = 0xC000_0000_0000_0000;

    /// <summary>
    /// Root node of the call tree.
    /// </summary>
    public required RootNode Root { get; init; }
    
    /// <summary>
    /// Lookup for formatted image addresses and uncompressed image info.
    /// The latter is used for the machine-readable result dump, which preserves image offsets for further processing
    /// (e.g., highlighting the respective lines in a source file).
    /// </summary>
    public required  Dictionary<ulong, (string formatted, string imageName, uint offset)> ImageAddresses  { get; init; }

    /// <summary>
    /// Lookup for formatted heap/stack addresses.
    /// </summary>
    public required Dictionary<ulong, string> MemoryAddresses { get; init; }
}