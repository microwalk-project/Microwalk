using System;
using System.Threading.Tasks;

namespace Microwalk
{
    public interface ILogger : IDisposable
    {
        /// <summary>
        /// Logs the given debug message.
        /// </summary>
        /// <param name="message">Message.</param>
        Task LogDebugAsync(string message);

        /// <summary>
        /// Logs the given warning.
        /// </summary>
        /// <param name="message">Message.</param>
        Task LogWarningAsync(string message);

        /// <summary>
        /// Logs the given error.
        /// </summary>
        /// <param name="message">Message.</param>
        Task LogErrorAsync(string message);

        /// <summary>
        /// Logs the given result.
        /// </summary>
        /// <param name="message">Message.</param>
        Task LogResultAsync(string message);

        /// <summary>
        /// Logs the given info message.
        /// Use this for status messages, which should always be displayed.
        /// </summary>
        /// <param name="message">Message.</param>
        Task LogInfoAsync(string message);
    }
}