using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk
{
    /// <summary>
    /// Used when encountering problems during (de)serializing and handling <see cref="TraceFile"/> objects.
    /// </summary>
    class TraceFormatException : Exception
    {
        /// <summary>
        /// Creates a new exception with the given message.
        /// </summary>
        /// <param name="message">Message.</param>
        public TraceFormatException(string message)
            : base(message) { }
    }
}
