using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Stages
{
    /// <summary>
    /// Abstract base class for the analysis stage.
    /// </summary>
    /// <remarks>
    /// This stage functions as a sink; it receives a stream of <see cref="TraceEntity"/> objects, which might be processed on the fly, or stored for later processing.
    /// After processing all traces, the function <see cref="FinishAsync"/> is called by the pipeline implementation; this may either be a no-op, or perform final analysis steps like outputting results.
    /// </remarks>
    public abstract class AnalysisStage : PipelineStage
    {
        /// <summary>
        /// Factory object for modules implementing this stage.
        /// </summary>
        public static ModuleFactory<AnalysisStage> Factory { get; } = new();

        /// <summary>
        /// Adds the given <see cref="TraceEntity"/> object to the analysis state. This method is expected to be thread-safe.
        /// </summary>
        /// <param name="traceEntity">Object containing trace data (must not be modified).</param>
        public abstract Task AddTraceAsync(TraceEntity traceEntity);

        /// <summary>
        /// Performs final analysis steps (e.g. outputting results). This function is called exactly once, after the trace pipeline is done.
        /// </summary>
        public abstract Task FinishAsync();
    }
}