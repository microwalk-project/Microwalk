using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Stages
{
    /// <summary>
    /// Abstract base class for the trace generation stage.
    /// </summary>
    public abstract class TraceStage : PipelineStage
    {
        /// <summary>
        /// Factory object for modules implementing this stage.
        /// </summary>
        public static ModuleFactory<TraceStage> Factory { get; } = new();

        /// <summary>
        /// Generates a trace for the testcase of the given <see cref="TraceEntity"/> object, and updates it. This method is expected to be thread-safe.
        /// </summary>
        /// <param name="traceEntity">The trace entity containing the test case data.</param>
        /// <returns></returns>
        public abstract Task GenerateTraceAsync(TraceEntity traceEntity);
    }
}