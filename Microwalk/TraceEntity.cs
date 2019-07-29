using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk
{
    /// <summary>
    /// Contains metadata for a single test case and the associated trace.
    /// Objects of this or derived classes are the only information handed between pipeline stages.
    /// 
    /// Interfaces defined in this class must be filled properly by all stages.
    /// </summary>
    class TraceEntity
    {
        /// <summary>
        /// The associated testcase file.
        /// </summary>
        public string TestcaseFile { get; set; }

        /// <summary>
        /// The associated trace file.
        /// </summary>
        public string TraceFile { get; set; }
    }
}
