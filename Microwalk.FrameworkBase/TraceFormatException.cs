using System;
using Microwalk.FrameworkBase.TraceFormat;

namespace Microwalk.FrameworkBase
{
    /// <summary>
    /// Used when encountering problems during (de)serializing and handling <see cref="TraceFile"/> objects.
    /// </summary>
    public class TraceFormatException : Exception
    {
        /// <summary>
        /// Creates a new exception with the given message.
        /// </summary>
        /// <param name="message">Message.</param>
        public TraceFormatException(string message)
            : base(message)
        {
        }
    }
}