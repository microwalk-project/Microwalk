namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// The different types of trace entries.
    /// </summary>
    public enum TraceEntryTypes : byte
    {
        /// <summary>
        /// An access to image file memory (usually .data or .r[o]data sections).
        /// </summary>
        ImageMemoryAccess = 1,

        /// <summary>
        /// An access to memory allocated on the heap.
        /// </summary>
        HeapMemoryAccess = 2,

        /// <summary>
        /// An access to memory allocated on the stack.
        /// </summary>
        StackMemoryAccess = 3,

        /// <summary>
        /// A heap allocation.
        /// </summary>
        HeapAllocation = 4,

        /// <summary>
        /// A memory free.
        /// </summary>
        HeapFree = 5,

        /// <summary>
        /// A code branch.
        /// </summary>
        Branch = 6,
        
        /// <summary>
        /// A stack allocation.
        /// </summary>
        StackAllocation = 7
    };
}