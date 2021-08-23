using Microwalk.FrameworkBase.TraceFormat;

namespace Microwalk.FrameworkBase
{
    /// <summary>
    /// Contains metadata for a single test case and the associated trace.
    /// Objects of this or derived classes are the only information handed between pipeline stages.
    /// 
    /// Interfaces defined in this class must be filled properly by all stages.
    /// </summary>
    public class TraceEntity
    {
        /// <summary>
        /// A unique number identifying this object.
        /// </summary>
        public int Id { get; init; }

        /// <summary>
        /// The associated testcase file.
        /// </summary>
        public string TestcaseFilePath { get; init; } = "";

        /// <summary>
        /// The associated raw trace file. May be null.
        /// </summary>
        public string? RawTraceFilePath { get; set; }

        /// <summary>
        /// The associated preprocessed trace file. May be null.
        /// </summary>
        public string? PreprocessedTraceFilePath { get; set; }

        /// <summary>
        /// The associated preprocessed trace file.
        /// </summary>
        public TraceFile? PreprocessedTraceFile { get; set; }
    }
}