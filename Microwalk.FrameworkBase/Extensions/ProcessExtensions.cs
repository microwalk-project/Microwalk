using System.Diagnostics;
using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Extensions
{
    /// <summary>
    /// Utility extension methods for the <see cref="Process"/> class.
    /// </summary>
    public static class ProcessExtensions
    {
        /// <summary>
        /// Waits asynchronously for the process to exit.
        /// </summary>
        /// <param name="process">The process to wait for cancellation.</param>
        /// <returns></returns>
        public static Task WaitForExitAsync(this Process process)
        {
            var tcs = new TaskCompletionSource<object?>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => tcs.SetResult(null);
            return tcs.Task;
        }
    }
}