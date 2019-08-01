using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk
{
    /// <summary>
    /// This exception type is used when the user passes an invalid configuration file.
    /// </summary>
    class ConfigurationException : Exception
    {
        /// <summary>
        /// Creates a new exception with the given message.
        /// </summary>
        /// <param name="message">Message.</param>
        public ConfigurationException(string message)
            : base(message) { }
    }
}
